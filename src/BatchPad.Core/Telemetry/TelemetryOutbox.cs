using System.Globalization;
using System.Text;
using System.Text.Json;
using BatchPad.Core.IO;

namespace BatchPad.Core.Telemetry;

/// <param name="File">Named <c>&lt;ticks&gt;-&lt;sequence&gt;.&lt;event count&gt;.json</c>, so names sort oldest first.</param>
public sealed record OutboxBatch(string File, int Count);

/// <summary>
/// Events waiting for delivery, one queue folder per sink and one file per batch, shared by every BatchPad process.
/// Past its size cap the oldest batches are dropped.
/// </summary>
public sealed class TelemetryOutbox(string directory, long maxBytes = TelemetryOutbox.DefaultMaxBytes)
{
    public const long DefaultMaxBytes = 20L * 1024 * 1024;

    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromMinutes(1);
    private static long _sequence;

    private readonly Lock _sizeLock = new();

    // Only this process's additions since the last trim; others' show up at the next one.
    private long? _approximateBytes;

    public string Directory { get; } = directory;

    public void Add(string sinkKey, IReadOnlyList<TelemetryEvent> events)
    {
        var folder = Path.Combine(Directory, sinkKey);
        System.IO.Directory.CreateDirectory(folder);
        var name = $"{DateTime.UtcNow.Ticks:D19}-{Interlocked.Increment(ref _sequence):D8}{Environment.ProcessId:X8}.{events.Count}.json";
        var json = JsonSerializer.Serialize(events, TelemetryEvent.Json);
        AtomicFile.WriteAllText(Path.Combine(folder, name), json, overwrite: false);
        lock (_sizeLock)
        {
            _approximateBytes = _approximateBytes is { } size ? size + Encoding.UTF8.GetByteCount(json) : Trim();
            if (_approximateBytes > maxBytes)
                _approximateBytes = Trim();
        }
    }

    public IReadOnlyList<OutboxBatch> Batches(string sinkKey)
    {
        var folder = Path.Combine(Directory, sinkKey);
        if (!System.IO.Directory.Exists(folder))
            return [];
        return [.. System.IO.Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal).Select(f => new OutboxBatch(f, CountOf(f)))];
    }

    public int Pending(string sinkKey) => Batches(sinkKey).Sum(b => b.Count);

    /// <summary>The batch's events; null when it is gone or corrupt.</summary>
    /// <exception cref="IOException">Someone else (a virus scanner, say) has it open; try again later.</exception>
    public static List<TelemetryEvent>? Read(OutboxBatch batch)
    {
        try
        {
            return JsonSerializer.Deserialize<List<TelemetryEvent>>(File.ReadAllText(batch.File), TelemetryEvent.Json);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return null;
        }
    }

    public static void Remove(OutboxBatch batch) => Delete(batch.File);

    /// <summary>Drops the queues of sinks no longer configured.</summary>
    public void KeepOnly(IEnumerable<string> sinkKeys)
    {
        if (!System.IO.Directory.Exists(Directory))
            return;
        var keep = sinkKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory))
        {
            if (keep.Contains(Path.GetFileName(folder)))
                continue;
            try
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Drops the oldest batches past the cap and temporary files a crashed writer left.</summary>
    /// <returns>The size of what is left.</returns>
    private long Trim()
    {
        var abandoned = DateTime.UtcNow - AbandonedAfter;
        var batches = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(Directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Extension == ".json")
                batches.Add(file);
            else if (file.Extension == AtomicFile.TemporaryExtension && file.LastWriteTimeUtc < abandoned)
                Delete(file.FullName);
        }
        batches.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        var total = batches.Sum(f => f.Length);
        foreach (var file in batches)
        {
            if (total <= maxBytes)
                break;
            total -= file.Length;
            Delete(file.FullName);
        }
        return total;
    }

    private static int CountOf(string file) =>
        int.TryParse(Path.GetExtension(Path.GetFileNameWithoutExtension(file)).TrimStart('.'), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count : 1;

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
}
