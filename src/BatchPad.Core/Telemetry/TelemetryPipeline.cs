using System.Security.Cryptography;
using BatchPad.Core.Config;
using BatchPad.Core.History;
using BatchPad.Core.IO;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Telemetry;

/// <summary>
/// Turns this process's run records into events in the outbox, and delivers the outbox to each configured sink in the
/// background with exponential backoff (§4.4). Nothing here can delay or fail a run.
/// </summary>
public sealed class TelemetryPipeline : IAsyncDisposable
{
    public const int MaxBatchEvents = 100;
    public const string TestTrigger = "test";
    public static readonly TimeSpan ExitFlushLimit = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan LongestRetry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan IdleRecheck = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan BusyRetry = TimeSpan.FromSeconds(10);

    private readonly Func<TelemetryOptions?> _options;
    private readonly Func<SinkConfig, ITelemetrySink> _createSink;
    private readonly SemaphoreSlim _delivering = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();

    // Only touched while holding _delivering.
    private readonly Dictionary<string, (int Failures, DateTimeOffset NotBefore)> _backoff = [];
    private readonly Dictionary<string, (string Config, ITelemetrySink Sink)> _sinks = [];

    private Task? _loop;
    private string? _salt;

    public TelemetryPipeline(string directory, Func<TelemetryOptions?> options, TimeProvider? time = null,
        Func<SinkConfig, ITelemetrySink>? createSink = null, long outboxMaxBytes = TelemetryOutbox.DefaultMaxBytes)
    {
        Directory = directory;
        _options = options;
        Time = time ?? TimeProvider.System;
        _createSink = createSink ?? SinkFactory.Create;
        Outbox = new TelemetryOutbox(Path.Combine(directory, "outbox"), outboxMaxBytes);
        StatusFile = new SinkStatusFile(Path.Combine(directory, "status.json"));
    }

    public static TelemetryPipeline For(AppPaths paths, Settings settings, TimeProvider? time = null) =>
        new(Path.Combine(paths.LocalDirectory, "telemetry"), () => settings.Telemetry, time);

    public string Directory { get; }
    public TimeProvider Time { get; }
    public TelemetryOutbox Outbox { get; }
    public SinkStatusFile StatusFile { get; }

    /// <summary>Raised on a worker thread after a delivery attempt changed the sinks' status.</summary>
    public event Action? StatusChanged;

    /// <summary>Queues the records <paramref name="store"/> saves from now on, until the subscription is disposed.</summary>
    public TelemetrySubscription Attach(HistoryStore store, TelemetryWorkspace workspace)
    {
        Action<RunRecord> onSaved = record => Enqueue(record, workspace);
        store.RunSaved += onSaved;
        return new TelemetrySubscription(this, () => store.RunSaved -= onSaved);
    }

    public void Enqueue(RunRecord record, TelemetryWorkspace workspace)
    {
        try
        {
            if (_options() is not { } options || Enabled(options) is not { Count: > 0 } sinks)
                return;
            TelemetryEvent[] events = [TelemetryEvents.From(record, workspace, options, options.HashNames ? Salt() : "")];
            foreach (var sink in sinks)
                Outbox.Add(sink.Key, events);
        }
        catch (Exception)
        {
            // Bad settings, a full disk or an unwritable folder cost the event, never the run.
        }
        Wake();
    }

    /// <summary>Starts delivering in the background, including what earlier processes left in the outbox.</summary>
    public void Start() => _loop ??= Task.Run(() => LoopAsync(_stop.Token));

    /// <summary>Delivers what is pending now, ignoring backoff, for at most <paramref name="limit"/>; the rest stays in the outbox.</summary>
    public async Task FlushAsync(TimeSpan limit)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        cancel.CancelAfter(limit);
        try
        {
            await DeliverAsync(ignoreBackoff: true, cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public IReadOnlyList<SinkStatus> Statuses()
    {
        var saved = StatusFile.Load();
        return [.. (_options()?.Sinks ?? []).Select(sink =>
        {
            var key = sink.Key;
            return (saved.GetValueOrDefault(key) ?? new SinkStatus(key, sink.Type, sink.Target))
                with { Type = sink.Type, Target = sink.Target, Pending = Outbox.Pending(key) };
        })];
    }

    /// <summary>Sends one test event straight to <paramref name="sink"/>, bypassing the outbox.</summary>
    /// <returns>Null when it arrived, else the error.</returns>
    public async Task<string?> SendTestEventAsync(SinkConfig sink, CancellationToken cancellationToken = default)
    {
        var now = Time.GetUtcNow();
        var record = new RunRecord
        {
            Id = RunRecord.NewId(now),
            NodeKey = TestTrigger,
            NodeId = TestTrigger,
            Name = "BatchPad test event",
            Trigger = TestTrigger,
            StartedAt = now,
            Machine = Environment.MachineName,
            User = Environment.UserName,
            BatchPadVersion = HistoryStore.Version,
        };
        var options = _options() ?? new TelemetryOptions();
        var telemetryEvent = TelemetryEvents.From(record, new TelemetryWorkspace(TestTrigger, "BatchPad"), options, options.HashNames ? Salt() : "");
        string? error = null;
        ITelemetrySink? instance = null;
        try
        {
            instance = _createSink(sink);
            await instance.SendAsync([telemetryEvent], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = ex.Message;
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
        StatusFile.Update(sink, status => error is null
            ? status with { LastSuccess = Time.GetUtcNow() }
            : status with { LastError = error, LastErrorAt = Time.GetUtcNow() });
        StatusChanged?.Invoke();
        return error;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
            await _loop.ConfigureAwait(false);
        // A flush still sending past the wait keeps its sinks rather than having them disposed under it.
        if (!await _delivering.WaitAsync(ExitFlushLimit).ConfigureAwait(false))
            return;
        try
        {
            foreach (var (_, sink) in _sinks.Values)
                (sink as IDisposable)?.Dispose();
            _sinks.Clear();
        }
        finally
        {
            _delivering.Release();
        }
    }

    private static List<SinkConfig> Enabled(TelemetryOptions options) => [.. options.Sinks.Where(s => s.Enabled)];

    private void Wake()
    {
        try
        {
            if (_wake.CurrentCount == 0)
                _wake.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            DateTimeOffset? next;
            try
            {
                next = await DeliverAsync(ignoreBackoff: false, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                next = Time.GetUtcNow() + BusyRetry;
            }

            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop);
            var woken = _wake.WaitAsync(waiting.Token);
            // Rechecking while idle picks up what other processes queued but could not deliver.
            var delay = next is { } due ? Max(TimeSpan.Zero, due - Time.GetUtcNow()) : IdleRecheck;
            var timer = Task.Delay(delay < IdleRecheck ? delay : IdleRecheck, Time, waiting.Token);
            await Task.WhenAny(woken, timer).ConfigureAwait(false);
            await waiting.CancelAsync().ConfigureAwait(false);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <returns>When a sink next needs another try; null when nothing is waiting on a retry.</returns>
    private async Task<DateTimeOffset?> DeliverAsync(bool ignoreBackoff, CancellationToken cancellationToken)
    {
        if (_options() is not { } options || !options.Sinks.Any(s => s.Enabled))
            return null;
        await _delivering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another process may be draining the same outbox; it'll deliver, so try later.
            using var sender = LockSender();
            if (sender is null)
                return Time.GetUtcNow() + BusyRetry;
            var configured = options.Sinks.Select(s => (Sink: s, Key: s.Key)).ToList();
            Outbox.KeepOnly(configured.Select(c => c.Key));
            DateTimeOffset? next = null;
            foreach (var (sink, key) in configured.Where(c => c.Sink.Enabled))
            {
                if (await DeliverAsync(sink, key, ignoreBackoff, cancellationToken).ConfigureAwait(false) is { } due && (next is null || due < next))
                    next = due;
            }
            return next;
        }
        finally
        {
            _delivering.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task<DateTimeOffset?> DeliverAsync(SinkConfig sink, string key, bool ignoreBackoff, CancellationToken cancellationToken)
    {
        if (!ignoreBackoff && _backoff.TryGetValue(key, out var waiting) && Time.GetUtcNow() < waiting.NotBefore)
            return waiting.NotBefore;
        Queue<OutboxBatch> queue;
        try
        {
            queue = new Queue<OutboxBatch>(Outbox.Batches(key));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Time.GetUtcNow() + BusyRetry;
        }
        List<OutboxBatch> sending = [];
        DateTimeOffset? next = null;
        DateTimeOffset? succeeded = null;
        (string Message, DateTimeOffset At)? error = null;
        try
        {
            ITelemetrySink? instance = null;
            while (true)
            {
                (sending, var events, var busy) = NextBatches(queue);
                if (busy)
                    next = Time.GetUtcNow() + BusyRetry;
                if (sending.Count == 0)
                    break;
                try
                {
                    instance ??= SinkFor(sink, key);
                    await instance.SendAsync(events, cancellationToken).ConfigureAwait(false);
                    sending.ForEach(TelemetryOutbox.Remove);
                    _backoff.Remove(key);
                    succeeded = Time.GetUtcNow();
                }
                catch (TelemetrySendException ex) when (!ex.Retry)
                {
                    sending.ForEach(TelemetryOutbox.Remove);
                    error = ($"{ex.Message} ({events.Count} events dropped)", Time.GetUtcNow());
                }
                sending = [];
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var failures = (_backoff.TryGetValue(key, out var previous) ? previous.Failures : 0) + 1;
            next = Time.GetUtcNow() + RetryDelay(failures);
            _backoff[key] = (failures, next.Value);
            error = (ex.Message, Time.GetUtcNow());
        }
        finally
        {
            if (succeeded is not null || error is not null)
            {
                var pending = queue.Sum(b => b.Count) + sending.Sum(b => b.Count);
                StatusFile.Update(sink, s => (error is { } failed ? s with { LastError = failed.Message, LastErrorAt = failed.At } : s)
                    with { LastSuccess = succeeded ?? s.LastSuccess, Pending = pending });
            }
        }
        return next;
    }

    public static TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Clamp(failures - 1, 0, 16)));

    /// <summary>Takes up to <see cref="MaxBatchEvents"/> events' worth of batches off <paramref name="queue"/>, removing corrupt ones.</summary>
    /// <returns>Busy when the first batch is open elsewhere (a virus scanner, say), so it waits for a retry instead of being lost.</returns>
    private static (List<OutboxBatch> Batches, List<TelemetryEvent> Events, bool Busy) NextBatches(Queue<OutboxBatch> queue)
    {
        var batches = new List<OutboxBatch>();
        var events = new List<TelemetryEvent>();
        while (events.Count < MaxBatchEvents && queue.TryPeek(out var batch))
        {
            List<TelemetryEvent>? read;
            try
            {
                read = TelemetryOutbox.Read(batch);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (batches, events, batches.Count == 0);
            }
            queue.Dequeue();
            if (read is null)
            {
                TelemetryOutbox.Remove(batch);
                continue;
            }
            batches.Add(batch);
            events.AddRange(read);
        }
        return (batches, events, false);
    }

    private ITelemetrySink SinkFor(SinkConfig sink, string key)
    {
        var config = ConfigJson.Serialize(sink);
        if (_sinks.TryGetValue(key, out var cached) && cached.Config == config)
            return cached.Sink;
        (cached.Sink as IDisposable)?.Dispose();
        var created = _createSink(sink);
        _sinks[key] = (config, created);
        return created;
    }

    private FileStream? LockSender()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            return new FileStream(Path.Combine(Directory, "sender.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string Salt()
    {
        if (_salt is not null)
            return _salt;
        var file = Path.Combine(Directory, "salt");
        try
        {
            if (!File.Exists(file))
            {
                System.IO.Directory.CreateDirectory(Directory);
                try
                {
                    AtomicFile.WriteAllText(file, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), overwrite: false);
                }
                catch (IOException)
                {
                    // Another process wrote it first.
                }
            }
            return _salt = File.ReadAllText(file).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not cached: the shared salt is tried again next time, and names still never go out in clear.
            return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }
    }
}

/// <summary>Ends a <see cref="TelemetryPipeline.Attach"/>. Disposing it asynchronously also flushes briefly, as a command-line process does before exiting.</summary>
public sealed class TelemetrySubscription(TelemetryPipeline pipeline, Action detach) : IDisposable, IAsyncDisposable
{
    public void Dispose() => detach();

    public async ValueTask DisposeAsync()
    {
        detach();
        await pipeline.FlushAsync(TelemetryPipeline.ExitFlushLimit).ConfigureAwait(false);
    }
}
