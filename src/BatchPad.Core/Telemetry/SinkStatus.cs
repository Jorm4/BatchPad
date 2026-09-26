using System.Text.Json;
using System.Text.Json.Serialization;

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
            var current = all.GetValueOrDefault(sink.Key) ?? new SinkStatus(sink.Key, sink.Type, sink.Target);
            all[sink.Key] = change(current with { Type = sink.Type, Target = sink.Target });
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var temporary = Path + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(all, TelemetryEvent.Json));
                File.Move(temporary, Path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
