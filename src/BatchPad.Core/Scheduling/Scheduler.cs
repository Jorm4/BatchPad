using System.Text.Json;
using BatchPad.Core.Config;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;

namespace BatchPad.Core.Scheduling;

public sealed record ScheduleFire(ScheduleEntry Entry, IRunOutput Run, Task<RunRecord> Recorded, RunRequest? Request = null);

/// <summary>A scheduled run that could not start (<see cref="Record"/> is its FailedToStart record, if it has a target) or that failed.</summary>
public sealed record ScheduleFailure(ScheduleEntry Entry, string Message, RunRecord? Record);

public sealed record SchedulePause(ScheduleEntry Entry, string DefinitionHash);

public sealed record ScheduleStatus(ScheduleEntry Entry, DateTimeOffset? NextFire, bool Paused, int Running, bool Queued, string? DefinitionHash);

/// <summary>Watches for an event trigger kind (file changes, start, another run) and fires the schedules that use it.</summary>
public interface ITriggerSource
{
    bool Handles(TriggerKind kind);

    /// <summary>Starts watching for <paramref name="entry"/>'s trigger, calling <paramref name="fire"/> each time it happens.</summary>
    /// <returns>Stops the watch.</returns>
    IDisposable Watch(ScheduleEntry entry, Func<bool> fire);
}

/// <summary>
/// Runs schedules unattended (§4.2): arms time triggers, applies <c>missed</c> and <c>overlap</c>, pauses a schedule whose
/// target changed until it is re-confirmed, and records each run in history with the schedule as its trigger.
/// Schedules that run in Windows are only listed and checked; Task Scheduler fires them.
/// </summary>
public sealed class Scheduler : IDisposable
{
    // Re-read the clock at least this often, so sleep and clock changes don't leave a fire waiting.
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(1);

    private readonly HistoryStore _history;
    private readonly IScheduleLauncher _launcher;
    private readonly ScheduleStateStore _state;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _zone;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Item> _items = [];
    private readonly Dictionary<string, string> _confirmedHashes = [];
    private readonly List<ITriggerSource> _sources = [];
    private ITimer? _timer;
    private bool _disposed;

    public Scheduler(HistoryStore history, IScheduleLauncher launcher, ScheduleStateStore state, TimeProvider? time = null, TimeZoneInfo? zone = null)
    {
        _history = history;
        _launcher = launcher;
        _state = state;
        _time = time ?? TimeProvider.System;
        _zone = zone ?? _time.LocalTimeZone;
    }

    public HistoryStore History => _history;
    public TimeProvider Time => _time;

    /// <summary>Fills scripts' missing secrets; workflows get theirs from the launcher's <see cref="WorkflowRunner"/>.</summary>
    public ISecretStore Secrets { get; init; } = ISecretStore.None;

    public event Action<ScheduleFire>? ScheduleFired;
    public event Action<ScheduleFailure>? ScheduleFailed;
    public event Action<SchedulePause>? SchedulePaused;

    /// <summary>Adds an event trigger source; it applies from the next <see cref="Start"/> or <see cref="Update"/>.</summary>
    public void AddSource(ITriggerSource source)
    {
        lock (_lock)
            _sources.Add(source);
    }

    /// <summary>Loads the schedules and runs, once, each <c>runOnce</c> one that fell due while nothing was running them.</summary>
    public void Start(IEnumerable<ScheduleEntry> entries) => Load(entries, applyMissed: true);

    /// <summary>Replaces the schedules, e.g. after a reload; runs in progress and queued fires carry over by key.</summary>
    public void Update(IEnumerable<ScheduleEntry> entries) => Load(entries, applyMissed: false);

    public IReadOnlyList<ScheduleStatus> Statuses()
    {
        lock (_lock)
            return [.. _items.Values.Select(i => new ScheduleStatus(i.Entry, i.Next, i.Paused, i.Running, i.Queued, i.Hash))];
    }

    /// <summary>Runs the schedule now, even when disabled; not while it is paused. Overlap still applies.</summary>
    public bool RunNow(string key) => Find(key) is { } item && Launch(item, manual: true);

    /// <summary>For trigger sources: fires the schedule as its trigger would.</summary>
    public bool Fire(string key) => Find(key) is { } item && Launch(item, manual: false);

    /// <summary>For trigger sources: reports a fire that was refused, e.g. to stop a loop, as a <see cref="ScheduleFailed"/>.</summary>
    public void Refuse(string key, string reason)
    {
        if (Find(key) is { } item)
            ScheduleFailed?.Invoke(new ScheduleFailure(item.Entry, reason, null));
    }

    /// <summary>Accepts the target's current definition and resumes the schedule.</summary>
    /// <returns>The hash to save as the schedule's <c>definitionHash</c>, or null when the target isn't resolved.</returns>
    public string? Confirm(string key)
    {
        string? hash;
        lock (_lock)
        {
            if (!_items.TryGetValue(key, out var item) || (hash = item.Hash) is null)
                return null;
            _confirmedHashes[key] = hash;
            (item.Paused, item.Confirmed) = (false, true);
            if (IsActive(item) && item.Entry.Schedule.Trigger.IsTimed)
                item.Next = TriggerMath.NextFire(item.Entry.Schedule.Trigger, _time.GetUtcNow(), _zone);
        }
        Arm();
        return hash;
    }

    public void Dispose()
    {
        List<IDisposable> watches;
        lock (_lock)
        {
            _disposed = true;
            _timer?.Dispose();
            watches = [.. _items.Values.Select(i => i.Watch).OfType<IDisposable>()];
            _items.Clear();
        }
        foreach (var watch in watches)
            watch.Dispose();
    }

    private void Load(IEnumerable<ScheduleEntry> entries, bool applyMissed)
    {
        var hashes = entries.Select(e => (Entry: e, Hash: e.Target is { } target ? DefinitionHash.Of(target) : null)).ToList();
        var paused = new List<SchedulePause>();
        var missed = new List<Item>();
        var unwatch = new List<IDisposable>();
        var watch = new List<Item>();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _time.GetUtcNow();
            var previous = new Dictionary<string, Item>(_items);
            _items.Clear();
            foreach (var (entry, hash) in hashes)
            {
                var item = previous.Remove(entry.Key, out var existing) ? existing : new Item();
                var oldEntry = item.Entry;
                item.Entry = entry;
                _items[entry.Key] = item;

                var state = _state.Get(entry.Key);
                if (state is null)
                    _state.Set(entry.Key, state = new ScheduleState { Seen = now });
                var wasPaused = item.Paused;
                CheckDefinition(item, hash);
                if (item.Paused && !wasPaused)
                    paused.Add(new SchedulePause(entry, item.Hash!));
                state = _state.Get(entry.Key)!;

                var trigger = entry.Schedule.Trigger;
                var active = IsActive(item);
                item.Next = active && trigger.IsTimed ? TriggerMath.NextFire(trigger, now, _zone) : null;
                if (applyMissed && active && trigger.IsTimed
                    && TriggerMath.NextFire(trigger, state.LastFire ?? state.Seen, _zone) is { } due && due <= now)
                {
                    _state.Set(entry.Key, state with { LastFire = now });
                    if (entry.Schedule.Missed == MissedPolicy.RunOnce)
                        missed.Add(item);
                }

                if (item.Watch is not null && (!active || !SameWatch(oldEntry!, entry)))
                {
                    unwatch.Add(item.Watch);
                    item.Watch = null;
                }
                if (item.Watch is null && active && !trigger.IsTimed && _sources.Any(s => s.Handles(trigger.Kind)))
                    watch.Add(item);
            }
            unwatch.AddRange(previous.Values.Select(i => i.Watch).OfType<IDisposable>());
        }

        _state.Save();
        foreach (var old in unwatch)
            old.Dispose();
        foreach (var pause in paused)
            SchedulePaused?.Invoke(pause);
        foreach (var item in watch)
            StartWatch(item);
        foreach (var item in missed)
            Launch(item, manual: false);
        Arm();
    }

    private static bool IsActive(Item item) => item.Entry.Schedule is { Enabled: true, RunIn: RunIn.App } && item.Entry.Problem is null && !item.Paused;

    private void StartWatch(Item item)
    {
        var entry = item.Entry;
        var watch = _sources.First(s => s.Handles(entry.Schedule.Trigger.Kind)).Watch(entry, () => Fire(entry.Key));
        lock (_lock)
        {
            if (!_disposed && item.Watch is null && ReferenceEquals(item.Entry, entry) && IsActive(item) && _items.GetValueOrDefault(entry.Key) == item)
            {
                item.Watch = watch;
                return;
            }
        }
        watch.Dispose();
    }

    private void CheckDefinition(Item item, string? hash)
    {
        item.Hash = hash;
        var verdict = hash is null
            ? default
            : ScheduleGate.Check(item.Entry, hash, _state, _time.GetUtcNow(), _confirmedHashes.GetValueOrDefault(item.Entry.Key));
        (item.Paused, item.Confirmed) = (verdict.Paused, verdict.Confirmed);
    }

    private bool PausedByChange(Item item, string hash)
    {
        SchedulePause? pause = null;
        bool paused;
        lock (_lock)
        {
            if (hash == item.Hash || item.Entry.Target is null)
                return false;
            var wasPaused = item.Paused;
            CheckDefinition(item, hash);
            paused = item.Paused;
            if (paused && !wasPaused)
                pause = new SchedulePause(item.Entry, hash);
        }
        _state.Save();
        if (pause is not null)
            SchedulePaused?.Invoke(pause);
        return paused;
    }

    private static bool SameWatch(ScheduleEntry a, ScheduleEntry b) =>
        a.Schedule.Target == b.Schedule.Target
        && JsonSerializer.Serialize(a.Schedule.Trigger, ConfigJson.Options) == JsonSerializer.Serialize(b.Schedule.Trigger, ConfigJson.Options);

    private Item? Find(string key)
    {
        lock (_lock)
            return _items.GetValueOrDefault(key);
    }

    private void Arm()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            var next = _items.Values.Min(i => i.Next);
            if (next is null)
            {
                _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return;
            }
            var wait = next.Value - _time.GetUtcNow();
            wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > MaxWait ? MaxWait : wait;
            if (_timer is null)
                _timer = _time.CreateTimer(_ => OnTimer(), null, wait, Timeout.InfiniteTimeSpan);
            else
                _timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer()
    {
        var due = new List<Item>();
        lock (_lock)
        {
            if (_disposed)
                return;
            var now = _time.GetUtcNow();
            foreach (var (key, item) in _items)
            {
                if (item.Next is not { } next || next > now)
                    continue;
                item.Next = TriggerMath.NextFire(item.Entry.Schedule.Trigger, now, _zone);
                if (_state.Get(key) is { } state)
                    _state.Set(key, state with { LastFire = now });
                due.Add(item);
            }
        }
        _state.Save();
        foreach (var item in due)
            Launch(item, manual: false);
        Arm();
    }

    private bool Launch(Item item, bool manual)
    {
        if (item.Entry.Target is { } current && PausedByChange(item, DefinitionHash.Of(current)))
            return false;
        ScheduleEntry entry;
        bool confirmed;
        lock (_lock)
        {
            entry = item.Entry;
            if (_disposed || _items.GetValueOrDefault(entry.Key) != item || item.Paused || !manual && !entry.Schedule.Enabled)
                return false;
            if (item.Running > 0 && entry.Schedule.Overlap != OverlapPolicy.Parallel)
            {
                item.Queued |= entry.Schedule.Overlap == OverlapPolicy.Queue;
                return false;
            }
            if (entry.Target is not null)
                item.Running++;
            confirmed = item.Confirmed;
        }

        if (entry.Target is not { } target)
        {
            ScheduleFailed?.Invoke(new ScheduleFailure(entry, entry.Problem ?? $"'{entry.Schedule.Target}' was not found.", null));
            return false;
        }
        var trigger = entry.Schedule.Trigger.Kind == TriggerKind.AfterRun ? RunTriggers.AfterRunOf(entry.Key) : RunTriggers.Schedule(entry.Key);
        IRunOutput run;
        Task<RunRecord> recorded;
        RunRequest? request;
        try
        {
            (run, recorded, request) = StartRun(target, entry.Schedule, trigger, confirmed);
        }
        catch (Exception ex)
        {
            lock (_lock)
                item.Running--;
            var message = SecretMasker.Mask(ex.Message,
                SecretMasker.SecretValues(target.Definition, target.Workspace, [.. target.Values, .. entry.Schedule.Values ?? []]));
            var record = _history.Add(target.RecordTemplate(trigger) with
            {
                StartedAt = _time.GetLocalNow(),
                Outcome = RunOutcome.FailedToStart,
                ExitCode = -1,
            }, [message]);
            ScheduleFailed?.Invoke(new ScheduleFailure(entry, message, record));
            return false;
        }
        _ = FinishAsync(item, entry, recorded);
        ScheduleFired?.Invoke(new ScheduleFire(entry, run, recorded, request));
        return true;
    }

    private (IRunOutput Run, Task<RunRecord> Recorded, RunRequest? Request) StartRun(ScheduleTarget target, Schedule schedule, string trigger, bool confirmed)
    {
        var values = target.ValuesWith(schedule);
        if (target.Definition is ScriptNode script)
        {
            var request = SecretFill.Apply(new RunRequest(target.Workspace, target.Tree, script)
            {
                Values = values,
                ExtraArguments = target.ExtraArguments,
                Unattended = true,
                Confirmed = confirmed,
            }, Secrets);
            var chain = Prerequisites.WorkflowFor(request);
            var run = chain is not null ? _launcher.Start(chain, trigger) : _launcher.Start(request);
            return (run, HistoryRecorder.Attach(run, _history, request, target.NodeKey, trigger, target.Name), chain is null ? request : null);
        }

        var workflowRequest = new WorkflowRequest(target.Tree, (WorkflowNode)target.Definition)
        {
            Values = values,
            StepValues = target.StepValues,
            Unattended = true,
            Confirmed = confirmed,
        };
        var workflowRun = _launcher.Start(workflowRequest, trigger);
        return (workflowRun, HistoryRecorder.Attach(workflowRun, _history, target.RecordTemplate(trigger)), null);
    }

    private async Task FinishAsync(Item item, ScheduleEntry entry, Task<RunRecord> recorded)
    {
        RunRecord? record = null;
        string? error = null;
        try
        {
            record = await recorded.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        bool again;
        lock (_lock)
        {
            item.Running--;
            again = item.Queued && item.Running == 0;
            if (again)
                item.Queued = false;
        }
        if (error is not null)
            ScheduleFailed?.Invoke(new ScheduleFailure(entry, error, null));
        else if (record is { Succeeded: false, Outcome: not RunOutcome.Stopped })
            ScheduleFailed?.Invoke(new ScheduleFailure(entry, FailureText(record), record));
        if (again)
            Launch(item, manual: false);
    }

    private static string FailureText(RunRecord record) => record.Outcome switch
    {
        RunOutcome.TimedOut => $"'{record.Name}' timed out.",
        RunOutcome.FailedToStart => $"'{record.Name}' failed to start.",
        _ => $"'{record.Name}' failed with exit code {record.ExitCode}.",
    };

    private sealed class Item
    {
        public ScheduleEntry Entry = null!;
        public string? Hash;
        public bool Paused;
        public bool Confirmed;
        public DateTimeOffset? Next;
        public int Running;
        public bool Queued;
        public IDisposable? Watch;
    }
}
