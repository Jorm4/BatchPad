using System.Text.Json;
using System.Text.Json.Serialization;
using BatchPad.Core.IO;

namespace BatchPad.Core.Telemetry;

public sealed record SinkStatus(string Key, string Type, string Target)
{
    public DateTimeOffset? LastSuccess { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
    public string? LastError { get; init; }
    public int Pending { get; init; }

    [JsonIgnore]
    public bool IsFailing => LastErrorAt is { } failed && (LastSuccess is not { } succeeded || failed > succeeded);
}

/// <summary>Each sink's delivery state in <c>status.json</c>, so any BatchPad process can show it.</summary>
public sealed class SinkStatusFile(string path)
{
    private readonly Lock _lock = new();

    public string Path { get; } = path;

    public Dictionary<string, SinkStatus> Load()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(Path)
                    ? JsonSerializer.Deserialize<Dictionary<string, SinkStatus>>(File.ReadAllText(Path), TelemetryEvent.Json) ?? []
                    : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return [];
            }
        }
    }

    public void Update(SinkConfig sink, Func<SinkStatus, SinkStatus> change)
    {
        lock (_lock)
        {
            var all = Load();
            var key = sink.Key;
            var current = all.GetValueOrDefault(key) ?? new SinkStatus(key, sink.Type, sink.Target);
            all[key] = change(current with { Type = sink.Type, Target = sink.Target });
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                AtomicFile.WriteAllText(Path, JsonSerializer.Serialize(all, TelemetryEvent.Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
