using System.Security.Cryptography;
using BatchPad.Core.Config;
using BatchPad.Core.History;
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
        if (_options() is not { } options || Enabled(options) is not { Count: > 0 } sinks)
            return;
        try
        {
            TelemetryEvent[] events = [TelemetryEvents.From(record, workspace, options, options.HashNames ? Salt() : "")];
            foreach (var sink in sinks)
                Outbox.Add(sink.Key, events);
        }
        catch (Exception)
        {
            // A full disk or an unwritable folder costs the event, never the run.
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
            (saved.GetValueOrDefault(sink.Key) ?? new SinkStatus(sink.Key, sink.Type, sink.Target))
                with { Type = sink.Type, Target = sink.Target, Pending = Outbox.Pending(sink.Key) })];
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
        foreach (var (_, sink) in _sinks.Values)
            (sink as IDisposable)?.Dispose();
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
            var timer = next is { } due
                ? Task.Delay(Max(TimeSpan.Zero, due - Time.GetUtcNow()), Time, waiting.Token)
                : Task.Delay(Timeout.Infinite, waiting.Token);
            await Task.WhenAny(woken, timer).ConfigureAwait(false);
            await waiting.CancelAsync().ConfigureAwait(false);
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    /// <returns>When a sink next needs another try; null when nothing is waiting on a retry.</returns>
    private async Task<DateTimeOffset?> DeliverAsync(bool ignoreBackoff, CancellationToken cancellationToken)
    {
        if (_options() is not { } options || Enabled(options) is not { Count: > 0 } sinks)
            return null;
        await _delivering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another process may be draining the same outbox; it'll deliver, so try later.
            using var sender = LockSender();
            if (sender is null)
                return Time.GetUtcNow() + BusyRetry;
            Outbox.KeepOnly(options.Sinks.Select(s => s.Key));
            DateTimeOffset? next = null;
            foreach (var sink in sinks)
            {
                if (await DeliverAsync(sink, ignoreBackoff, cancellationToken).ConfigureAwait(false) is { } due && (next is null || due < next))
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

    private async Task<DateTimeOffset?> DeliverAsync(SinkConfig sink, bool ignoreBackoff, CancellationToken cancellationToken)
    {
        if (!ignoreBackoff && _backoff.TryGetValue(sink.Key, out var waiting) && Time.GetUtcNow() < waiting.NotBefore)
            return waiting.NotBefore;
        while (true)
        {
            var (batches, events) = NextBatches(sink.Key);
            if (batches.Count == 0)
                return null;
            try
            {
                await SinkFor(sink).SendAsync(events, cancellationToken).ConfigureAwait(false);
                batches.ForEach(TelemetryOutbox.Remove);
                _backoff.Remove(sink.Key);
                StatusFile.Update(sink, s => s with { LastSuccess = Time.GetUtcNow(), Pending = Outbox.Pending(sink.Key) });
            }
            catch (TelemetrySendException ex) when (!ex.Retry)
            {
                batches.ForEach(TelemetryOutbox.Remove);
                Failed(sink, $"{ex.Message} ({events.Count} events dropped)");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var failures = (_backoff.TryGetValue(sink.Key, out var previous) ? previous.Failures : 0) + 1;
                var notBefore = Time.GetUtcNow() + RetryDelay(failures);
                _backoff[sink.Key] = (failures, notBefore);
                Failed(sink, ex.Message);
                return notBefore;
            }
        }
    }

    public static TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromTicks(Math.Min(LongestRetry.Ticks, FirstRetry.Ticks << Math.Clamp(failures - 1, 0, 16)));

    private void Failed(SinkConfig sink, string error) =>
        StatusFile.Update(sink, s => s with { LastError = error, LastErrorAt = Time.GetUtcNow(), Pending = Outbox.Pending(sink.Key) });

    private (List<OutboxBatch> Batches, List<TelemetryEvent> Events) NextBatches(string sinkKey)
    {
        var batches = new List<OutboxBatch>();
        var events = new List<TelemetryEvent>();
        foreach (var batch in Outbox.Batches(sinkKey))
        {
            if (events.Count >= MaxBatchEvents)
                break;
            if (TelemetryOutbox.Read(batch) is not { } read)
            {
                TelemetryOutbox.Remove(batch);
                continue;
            }
            batches.Add(batch);
            events.AddRange(read);
        }
        return (batches, events);
    }

    private ITelemetrySink SinkFor(SinkConfig sink)
    {
        var config = ConfigJson.Serialize(sink);
        if (_sinks.TryGetValue(sink.Key, out var cached) && cached.Config == config)
            return cached.Sink;
        (cached.Sink as IDisposable)?.Dispose();
        var created = _createSink(sink);
        _sinks[sink.Key] = (config, created);
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
                var temporary = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(temporary, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));
                try
                {
                    File.Move(temporary, file);
                }
                catch (IOException)
                {
                    File.Delete(temporary);
                }
            }
            return _salt = File.ReadAllText(file).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
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
