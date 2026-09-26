using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>A long-running run as <c>running.json</c> keeps it, so a later session can adopt it.</summary>
public sealed record RunningEntry
{
    public required string WorkspaceFile { get; init; }
    public required string NodeKey { get; init; }
    public required string Name { get; init; }
    public int ProcessId { get; init; }

    /// <summary>Tells the process from a later one that reused its id.</summary>
    public DateTime ProcessStartedUtc { get; init; }

    public Dictionary<string, JsonNode?> Values { get; init; } = [];
    public string? ExtraArguments { get; init; }
}

/// <summary>
/// The long-running runs still alive, in <c>LocalDirectory\running.json</c> (§4 Stopping). Entries this instance
/// added or adopted are not adopted again, so reloading a workspace leaves them alone.
/// </summary>
public sealed class RunningRegistry(string filePath)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Lock FileLock = new();

    private readonly HashSet<(int, DateTime)> _known = [];

    public static RunningRegistry For(AppPaths paths) => new(Path.Combine(paths.LocalDirectory, "running.json"));

    public string FilePath { get; } = filePath;

    /// <summary>Records <paramref name="entry"/> with its process's start time; null when the process has already exited.</summary>
    public RunningEntry? Add(RunningEntry entry)
    {
        if (StartTimeOf(entry.ProcessId) is not { } started)
            return null;
        var recorded = entry with { ProcessStartedUtc = started };
        lock (FileLock)
        {
            _known.Add((recorded.ProcessId, started));
            Save([.. Load().Where(e => e.ProcessId != recorded.ProcessId), recorded]);
        }
        return recorded;
    }

    public void Remove(RunningEntry entry)
    {
        lock (FileLock)
            Save([.. Load().Where(e => !Same(e, entry))]);
    }

    /// <summary>The workspace's runs still alive that this instance does not know yet. Entries whose process is gone are dropped.</summary>
    public IReadOnlyList<AdoptedRun> Adopt(string workspaceFile)
    {
        var adopted = new List<AdoptedRun>();
        lock (FileLock)
        {
            var alive = Load().Where(e => StartTimeOf(e.ProcessId) == e.ProcessStartedUtc).ToList();
            Save(alive);
            foreach (var entry in alive.Where(e => string.Equals(e.WorkspaceFile, workspaceFile, StringComparison.OrdinalIgnoreCase)))
            {
                if (!_known.Add((entry.ProcessId, entry.ProcessStartedUtc)))
                    continue;
                if (AdoptedRun.TryOpen(entry, this) is { } run)
                    adopted.Add(run);
            }
        }
        return adopted;
    }

    internal static DateTime? StartTimeOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool Same(RunningEntry a, RunningEntry b) => a.ProcessId == b.ProcessId && a.ProcessStartedUtc == b.ProcessStartedUtc;

    private List<RunningEntry> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<RunningEntry>>(File.ReadAllText(FilePath), Json) ?? [] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private void Save(List<RunningEntry> entries)
    {
        if (entries.Count == 0)
        {
            if (File.Exists(FilePath))
                File.Delete(FilePath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Json));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
