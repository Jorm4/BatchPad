using System.Text.Json;
using System.Text.Json.Serialization;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.History;

/// <summary>A workspace's finished runs: a JSON record and a log file each, pruned by count and age (§3.9).</summary>
public sealed class HistoryStore
{
    public const int DefaultMaxRecords = 500;
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly Lock _lock = new();
    private readonly int _maxRecords;
    private readonly TimeSpan _maxAge;
    private List<RunRecord>? _newestFirst;

    public HistoryStore(string directory, TimeProvider? time = null, int maxRecords = DefaultMaxRecords, TimeSpan? maxAge = null)
    {
        Directory = directory;
        Time = time ?? TimeProvider.System;
        _maxRecords = maxRecords;
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    public static HistoryStore For(AppPaths paths, string workspaceId, TimeProvider? time = null) =>
        new(System.IO.Path.Combine(paths.LocalDirectory, "history", workspaceId), time);

    public string Directory { get; }
    public TimeProvider Time { get; }

    /// <summary>Raised after a record is saved, on the thread that saved it.</summary>
    public event Action<RunRecord>? RunRecorded;

    public string LogPath(RunRecord record) => System.IO.Path.Combine(Directory, record.Id + ".log");

    public RunRecord Add(RunRecord record, IEnumerable<string> log)
    {
        var saved = record with { Id = $"{record.StartedAt.UtcDateTime:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..28] };
        lock (_lock)
        {
            var records = Records();
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllLines(LogPath(saved), log);
            File.WriteAllText(RecordPath(saved), JsonSerializer.Serialize(saved, Json));
            records.Insert(0, saved);
            records.Sort(NewestFirst);
            Prune(records);
        }
        RunRecorded?.Invoke(saved);
        return saved;
    }

    public IReadOnlyList<RunRecord> Recent(int count = int.MaxValue)
    {
        lock (_lock)
            return [.. Records().Take(count)];
    }

    /// <summary>Each node's latest run that was not stopped, since stopping a server is not a result.</summary>
    public IReadOnlyDictionary<string, RunRecord> LastResults()
    {
        lock (_lock)
        {
            var last = new Dictionary<string, RunRecord>();
            foreach (var record in Records().Where(r => r.Outcome != RunOutcome.Stopped))
                last.TryAdd(record.NodeKey, record);
            return last;
        }
    }

    private string RecordPath(RunRecord record) => System.IO.Path.Combine(Directory, record.Id + ".json");

    private List<RunRecord> Records()
    {
        if (_newestFirst is not null)
            return _newestFirst;
        _newestFirst = [];
        if (System.IO.Directory.Exists(Directory))
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
            {
                try
                {
                    if (JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), Json) is { } record)
                        _newestFirst.Add(record with { Id = System.IO.Path.GetFileNameWithoutExtension(file) });
                }
                catch (Exception ex) when (ex is JsonException or IOException)
                {
                }
            }
        }
        _newestFirst.Sort(NewestFirst);
        Prune(_newestFirst);
        return _newestFirst;
    }

    private void Prune(List<RunRecord> newestFirst)
    {
        var oldest = Time.GetUtcNow() - _maxAge;
        for (var i = newestFirst.Count - 1; i >= 0; i--)
        {
            if (i < _maxRecords && newestFirst[i].StartedAt >= oldest)
                continue;
            Delete(RecordPath(newestFirst[i]));
            Delete(LogPath(newestFirst[i]));
            newestFirst.RemoveAt(i);
        }
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }

    private static int NewestFirst(RunRecord a, RunRecord b) =>
        b.StartedAt.CompareTo(a.StartedAt) is var byTime and not 0 ? byTime : string.CompareOrdinal(b.Id, a.Id);
}
