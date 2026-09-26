using BatchPad.Core.Discovery;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Scheduling;

public static class EventTriggers
{
    /// <summary>Adds the <c>fileChanged</c>, <c>onStart</c> and <c>afterRun</c> sources to the scheduler, before it starts.</summary>
    /// <param name="watcher">Watches the workspace folder; without one, <c>fileChanged</c> schedules never fire.</param>
    /// <returns>Stops listening to the watcher and to history.</returns>
    public static IDisposable Register(Scheduler scheduler, LoadedWorkspace workspace, FolderWatcher? watcher)
    {
        var afterRun = new AfterRunTriggerSource(scheduler);
        scheduler.AddSource(new OnStartTriggerSource());
        scheduler.AddSource(afterRun);
        if (watcher is null)
            return afterRun;
        var files = new FileChangedTriggerSource(watcher, workspace.Directory, scheduler.Time);
        scheduler.AddSource(files);
        return new Stop(() =>
        {
            afterRun.Dispose();
            files.Dispose();
        });
    }
}

/// <summary>Fires each <c>onStart</c> schedule once, when it is first watched.</summary>
public sealed class OnStartTriggerSource : ITriggerSource
{
    private readonly HashSet<string> _started = [];

    public bool Handles(TriggerKind kind) => kind == TriggerKind.OnStart;

    public IDisposable Watch(ScheduleEntry entry, Action fire)
    {
        bool first;
        lock (_started)
            first = _started.Add(entry.Key);
        if (first)
            fire();
        return Stop.Nothing;
    }
}

/// <summary>Fires a <c>fileChanged</c> schedule once a burst of changes to files matching its glob has settled.</summary>
/// <remarks>The glob is relative to the base directory; a rename matches by its old or new path.</remarks>
public sealed class FileChangedTriggerSource : ITriggerSource, IDisposable
{
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(1);

    private readonly FolderWatcher _watcher;
    private readonly string _baseDirectory;
    private readonly TimeProvider _time;
    private readonly List<FileWatch> _watches = [];

    public FileChangedTriggerSource(FolderWatcher watcher, string baseDirectory, TimeProvider? time = null)
    {
        (_watcher, _baseDirectory, _time) = (watcher, baseDirectory, time ?? TimeProvider.System);
        watcher.FileChanged += OnFileChanged;
    }

    public bool Handles(TriggerKind kind) => kind == TriggerKind.FileChanged;

    public IDisposable Watch(ScheduleEntry entry, Action fire)
    {
        var trigger = entry.Schedule.Trigger;
        var debounce = trigger.Debounce is { } text ? TriggerMath.ParseDuration(text) ?? DefaultDebounce : DefaultDebounce;
        var watch = new FileWatch(trigger.FileChanged!, debounce, _time.CreateTimer(_ => fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        lock (_watches)
            _watches.Add(watch);
        return new Stop(() =>
        {
            lock (_watches)
                _watches.Remove(watch);
            watch.Timer.Dispose();
        });
    }

    public void Dispose() => _watcher.FileChanged -= OnFileChanged;

    private void OnFileChanged(object? sender, FileChange change)
    {
        var paths = new[] { change.FullPath, change.OldFullPath }.OfType<string>()
            .Select(path => Path.GetRelativePath(_baseDirectory, path))
            .Where(relative => !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar))
            .ToList();
        if (paths.Count == 0)
            return;
        lock (_watches)
        {
            foreach (var watch in _watches.Where(w => paths.Any(path => Glob.IsMatch(w.Pattern, path))))
                watch.Timer.Change(watch.Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private sealed record FileWatch(string Pattern, TimeSpan Debounce, ITimer Timer);
}

/// <summary>
/// Fires an <c>afterRun</c> schedule when a run of its node is recorded with a matching result (default success; stopped
/// runs never match). A schedule that already fired in the same cascade is refused, so a loop stops after one round.
/// </summary>
public sealed class AfterRunTriggerSource : ITriggerSource, IDisposable
{
    private static readonly string SchedulePrefix = RunTriggers.Schedule("");

    private readonly Scheduler _scheduler;
    private readonly Lock _lock = new();
    private readonly List<RunWatch> _watches = [];
    private readonly Dictionary<string, List<string>> _cascades = [];

    public AfterRunTriggerSource(Scheduler scheduler)
    {
        _scheduler = scheduler;
        scheduler.History.RunRecorded += OnRecorded;
    }

    public bool Handles(TriggerKind kind) => kind == TriggerKind.AfterRun;

    public IDisposable Watch(ScheduleEntry entry, Action fire)
    {
        var trigger = entry.Schedule.Trigger;
        if (entry is not { From: { } from, Target.Workspace: { } workspace }
            || ScheduleTarget.Resolve(trigger.AfterRun!, from, workspace, out _) is not { } after)
            return Stop.Nothing;
        var watch = new RunWatch(entry, after.NodeKey, trigger.Result ?? AfterRunResult.Success, fire);
        lock (_lock)
            _watches.Add(watch);
        return new Stop(() =>
        {
            lock (_lock)
                _watches.Remove(watch);
        });
    }

    public void Dispose() => _scheduler.History.RunRecorded -= OnRecorded;

    private void OnRecorded(RunRecord record)
    {
        if (record.Outcome == RunOutcome.Stopped)
            return;
        var fire = new List<RunWatch>();
        var refuse = new List<(RunWatch Watch, string Reason)>();
        lock (_lock)
        {
            List<string> cascade = record.Trigger.StartsWith(SchedulePrefix, StringComparison.Ordinal)
                && _cascades.Remove(record.Trigger[SchedulePrefix.Length..], out var causes) ? causes : [];
            foreach (var watch in _watches.Where(w => w.NodeKey == record.NodeKey && Matches(w.Result, record)))
            {
                var key = watch.Entry.Schedule.Key;
                if (cascade.Contains(key))
                {
                    refuse.Add((watch, $"Stopped an afterRun loop: {string.Join(" → ", cascade.Append(key))}."));
                    continue;
                }
                _cascades[key] = [.. cascade, key];
                fire.Add(watch);
            }
        }
        foreach (var watch in fire)
            watch.Fire();
        foreach (var (watch, reason) in refuse)
            _scheduler.Refuse(watch.Entry.Key, reason);
    }

    private static bool Matches(AfterRunResult result, RunRecord record) => result switch
    {
        AfterRunResult.Success => record.Succeeded,
        AfterRunResult.Failure => !record.Succeeded,
        _ => true,
    };

    private sealed record RunWatch(ScheduleEntry Entry, string NodeKey, AfterRunResult Result, Action Fire);
}

internal sealed class Stop(Action stop) : IDisposable
{
    public static readonly Stop Nothing = new(() => { });

    public void Dispose() => stop();
}
