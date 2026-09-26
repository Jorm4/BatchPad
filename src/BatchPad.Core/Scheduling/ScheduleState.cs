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

/// <summary>
/// <c>schedules.json</c> in the local-data folder, keyed by <see cref="ScheduleEntry.Key"/>. Changes are kept in memory
/// until <see cref="Save"/>. A file that can't be parsed is kept beside it as <c>.bad</c>.
/// </summary>
public sealed class ScheduleStateStore(string filePath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Lock _lock = new();
    private Dictionary<string, ScheduleState>? _states;
    private bool _changed;
    private bool _writable = true;
    private bool _loadFailed;

    public string FilePath { get; } = filePath;

    /// <summary>The file existed but could not be read, so states such as adopted hashes may have been lost.</summary>
    public bool LoadFailed
    {
        get
        {
            lock (_lock)
            {
                States();
                return _loadFailed;
            }
        }
    }

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
            _changed = true;
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            if (!_changed || !_writable)
                return;
            var temp = FilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(_states, Json));
                File.Move(temp, FilePath, overwrite: true);
                _changed = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
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
        catch (JsonException)
        {
            _loadFailed = true;
            KeepBadFile();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            (_loadFailed, _writable) = (true, false);
        }
        return _states ??= [];
    }

    private void KeepBadFile()
    {
        try
        {
            File.Move(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _writable = false;
        }
    }
}
