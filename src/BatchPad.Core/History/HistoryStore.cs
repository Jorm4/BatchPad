using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using BatchPad.Core.IO;

namespace BatchPad.Core.History;

/// <summary>
/// A workspace's finished runs: a JSON record and a log file each, pruned by count and age (§3.9). Records other
/// processes write to the same folder are picked up too, so the app sees command-line runs.
/// </summary>
public sealed class HistoryStore
{
    public const int DefaultMaxRecords = 500;
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static readonly string? Version = typeof(HistoryStore).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];

    private readonly Lock _lock = new();
    private readonly int _maxRecords;
    private readonly TimeSpan _maxAge;
    private List<RunRecord>? _newestFirst;

    // Every id this store has loaded or written, pruned ones included, so a file it failed to delete isn't news.
    private readonly HashSet<string> _known = [];
    private DateTime _scannedWriteTime;
    private Action<RunRecord>? _runRecorded;
    private FileSystemWatcher? _watcher;

    public HistoryStore(string directory, TimeProvider? time = null, int maxRecords = DefaultMaxRecords, TimeSpan? maxAge = null)
    {
        Directory = directory;
        Time = time ?? TimeProvider.System;
        _maxRecords = maxRecords;
        _maxAge = maxAge ?? DefaultMaxAge;
    }

    public static HistoryStore For(AppPaths paths, string workspaceId, TimeProvider? time = null) =>
        new(System.IO.Path.Combine(RootOf(paths), workspaceId), time);

    private static string RootOf(AppPaths paths) => System.IO.Path.Combine(paths.LocalDirectory, "history");

    public string Directory { get; }
    public TimeProvider Time { get; }

    /// <summary>
    /// Raised after a record is saved, on the thread that saved it, or on a worker thread when another process saved it.
    /// The folder is watched while this has handlers.
    /// </summary>
    public event Action<RunRecord>? RunRecorded
    {
        add
        {
            lock (_lock)
            {
                _runRecorded += value;
                Watch();
            }
        }
        remove
        {
            lock (_lock)
            {
                _runRecorded -= value;
                if (_runRecorded is null)
                    StopWatching();
            }
        }
    }

    /// <summary>Raised after this store saves a record, on the saving thread; never for other processes' records.</summary>
    public event Action<RunRecord>? RunSaved;

    public string LogPath(RunRecord record) => System.IO.Path.Combine(Directory, record.Id + ".log");

    /// <summary>Saves <paramref name="record"/> under its <see cref="RunRecord.Id"/>, or a new one when it has none.</summary>
    public RunRecord Add(RunRecord record, IEnumerable<string> log)
    {
        var saved = record with
        {
            Id = record.Id.Length > 0 ? record.Id : RunRecord.NewId(record.StartedAt),
            Machine = record.Machine ?? Environment.MachineName,
            User = record.User ?? Environment.UserName,
            BatchPadVersion = record.BatchPadVersion ?? Version,
        };
        Action<RunRecord>? handlers;
        lock (_lock)
        {
            var records = Records();
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllLines(LogPath(saved), log);
            AtomicFile.WriteAllText(RecordPath(saved), JsonSerializer.Serialize(saved, Json));
            _known.Add(saved.Id);
            InsertNewestFirst(records, saved);
            Prune(records);
            handlers = _runRecorded;
        }
        RunSaved?.Invoke(saved);
        handlers?.Invoke(saved);
        return saved;
    }

    public IReadOnlyList<RunRecord> Recent(int count = int.MaxValue) => Read<IReadOnlyList<RunRecord>>(records => [.. records.Take(count)]);

    public RunRecord? Find(string id) => Find(r => r.Id == id);

    /// <summary>The newest record that matches <paramref name="match"/>, or null.</summary>
    public RunRecord? Find(Func<RunRecord, bool> match) => Read(records => records.FirstOrDefault(match));

    /// <summary>Each node's latest run that was not stopped, since stopping a server is not a result.</summary>
    public IReadOnlyDictionary<string, RunRecord> LastResults() => Read(records =>
    {
        var last = new Dictionary<string, RunRecord>();
        foreach (var record in records.Where(r => r.Outcome != RunOutcome.Stopped))
            last.TryAdd(record.NodeKey, record);
        return last;
    });

    private string RecordPath(RunRecord record) => System.IO.Path.Combine(Directory, record.Id + ".json");

    private T Read<T>(Func<List<RunRecord>, T> read, bool force = false)
    {
        T result;
        IReadOnlyList<RunRecord> external;
        lock (_lock)
        {
            external = Refresh(force);
            result = read(Records());
        }
        Announce(external);
        return result;
    }

    private List<RunRecord> Records()
    {
        if (_newestFirst is null)
        {
            _newestFirst = [];
            TakeIn(force: true);
        }
        return _newestFirst;
    }

    /// <summary>Takes in what other processes added or pruned since the last look; returns the added records to announce.</summary>
    private List<RunRecord> Refresh(bool force)
    {
        if (_newestFirst is null)
        {
            Records();
            return [];
        }
        return TakeIn(force);
    }

    private List<RunRecord> TakeIn(bool force)
    {
        var writeTime = DirectoryWriteTime();
        if (writeTime == _scannedWriteTime && !force || RecordFiles() is not { } files)
            return [];

        var added = new List<RunRecord>();
        var unreadable = false;
        foreach (var (id, file) in files)
        {
            if (_known.Contains(id))
                continue;
            if (!TryLoad(id, file, out var record))
            {
                unreadable = true;
                continue;
            }
            _known.Add(id);
            if (record is not null)
                added.Add(record);
        }
        // Rescan on the next look, so a file another process briefly holds isn't missed until the folder changes again.
        if (!unreadable)
            _scannedWriteTime = writeTime;
        _newestFirst!.RemoveAll(r => !files.ContainsKey(r.Id));
        foreach (var record in added)
            InsertNewestFirst(_newestFirst, record);
        return [.. added.OrderBy(r => r.StartedAt)];
    }

    private void Announce(IReadOnlyList<RunRecord> external)
    {
        if (external.Count == 0)
            return;
        Action<RunRecord>? handlers;
        lock (_lock)
            handlers = _runRecorded;
        foreach (var record in external)
            handlers?.Invoke(record);
    }

    /// <summary>The record files by id; null when the folder can't be listed right now.</summary>
    private Dictionary<string, string>? RecordFiles()
    {
        try
        {
            return System.IO.Directory.Exists(Directory)
                ? System.IO.Directory.EnumerateFiles(Directory, "*.json").ToDictionary(file => System.IO.Path.GetFileNameWithoutExtension(file))
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <returns>False when the file can't be read right now; one that isn't a valid record reads as null.</returns>
    private static bool TryLoad(string id, string file, out RunRecord? record)
    {
        record = null;
        try
        {
            record = JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(file), Json) is { } loaded ? loaded with { Id = id } : null;
            return true;
        }
        catch (JsonException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private DateTime DirectoryWriteTime()
    {
        try
        {
            return System.IO.Directory.GetLastWriteTimeUtc(Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private void Watch()
    {
        if (_watcher is not null)
            return;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            _watcher = new FileSystemWatcher(Directory, "*.json") { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return;
        }
        _watcher.Created += OnChanged;
        _watcher.Changed += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Error += (_, _) => OnChanged(null, null);
        _watcher.EnableRaisingEvents = true;
    }

    private void StopWatching()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnChanged(object? sender, FileSystemEventArgs? change) => Read(_ => 0, force: true);

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void InsertNewestFirst(List<RunRecord> records, RunRecord record)
    {
        var index = records.BinarySearch(record, NewestFirstComparer);
        records.Insert(index < 0 ? ~index : index, record);
    }

    private static readonly Comparer<RunRecord> NewestFirstComparer = Comparer<RunRecord>.Create(NewestFirst);

    private static int NewestFirst(RunRecord a, RunRecord b) =>
        b.StartedAt.CompareTo(a.StartedAt) is var byTime and not 0 ? byTime : string.CompareOrdinal(b.Id, a.Id);
}
