using System.Text.Json;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Scheduling;

/// <summary>What the scheduler remembers about one schedule between sessions.</summary>
public sealed record ScheduleState
{
    /// <summary>When the schedule was first loaded; missed fires count from here until it has fired.</summary>
    public DateTimeOffset Seen { get; init; }

    /// <summary>The last time a time trigger fell due and was handled, whether it ran or was skipped.</summary>
    public DateTimeOffset? LastFire { get; init; }

    /// <summary>The definition a schedule without <c>definitionHash</c> was first seen with.</summary>
    public string? AdoptedHash { get; init; }
}

/// <summary><c>schedules.json</c> in the local-data folder, keyed by <see cref="ScheduleEntry.Key"/>.</summary>
public sealed class ScheduleStateStore(string filePath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private Dictionary<string, ScheduleState>? _states;

    public string FilePath { get; } = filePath;

    public static ScheduleStateStore For(AppPaths paths) => new(Path.Combine(paths.LocalDirectory, "schedules.json"));

    public ScheduleState? Get(string key)
    {
        lock (_lock)
            return States().GetValueOrDefault(key);
    }

    public void Set(string key, ScheduleState state)
    {
        lock (_lock)
        {
            States()[key] = state;
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_states, Json));
        }
    }

    private Dictionary<string, ScheduleState> States()
    {
        if (_states is not null)
            return _states;
        try
        {
            _states = File.Exists(FilePath) ? JsonSerializer.Deserialize<Dictionary<string, ScheduleState>>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
        }
        return _states ??= [];
    }
}
