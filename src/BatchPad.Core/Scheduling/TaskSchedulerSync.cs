using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Scheduling;

public enum WindowsTaskState { Registered, NotRegistered, Failed, OtherWorkspace }

/// <param name="Reason">Why the task isn't registered, the registrar's error, or the other workspace file whose task it is.</param>
public sealed record WindowsTaskStatus(string TaskName, WindowsTaskState State, DateTimeOffset? NextRun = null, string? Reason = null)
{
    public DateTimeOffset At { get; init; }

    /// <summary><paramref name="synced"/>, unless a run from the task failed to register the next fire since.</summary>
    public static WindowsTaskStatus? Latest(WindowsTaskStatus? synced, WindowsTaskFailure? failure) =>
        failure is null || synced is not null && synced.At >= failure.At ? synced
        : new WindowsTaskStatus(synced?.TaskName ?? "", WindowsTaskState.Failed, Reason: failure.Reason) { At = failure.At };
}

/// <summary>
/// Keeps one Task Scheduler task per schedule that runs in Windows, registered at its next fire, and removes the tasks
/// of schedules that were deleted, disabled or moved back to the app. Only confirmed schedules of trusted workspaces register.
/// Tasks registered by another workspace file with the same id are left alone.
/// </summary>
public sealed class TaskSchedulerSync(ITaskRegistrar registrar, TaskHost host, TimeProvider? time = null, TimeZoneInfo? zone = null)
{
    public const string WaitingForConfirm = "Waiting for confirm";

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly TimeZoneInfo _zone = zone ?? (time ?? TimeProvider.System).LocalTimeZone;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _registered = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queried = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, WindowsTaskStatus> _statuses = [];

    public ITaskRegistrar Registrar => registrar;

    /// <summary>The last sync's result, by <see cref="ScheduleEntry.Key"/>.</summary>
    public WindowsTaskStatus? StatusOf(string entryKey)
    {
        lock (_lock)
            return _statuses.GetValueOrDefault(entryKey);
    }

    public void Sync(LoadedWorkspace workspace, IEnumerable<ScheduleEntry> entries, bool trusted)
    {
        lock (_lock)
        {
            var folder = host.FolderOf(workspace.Id);
            var windows = entries.Where(e => e.Schedule.RunIn == RunIn.Windows).ToList();
            _statuses = [];
            if (windows.Count == 0 && _queried.Contains(workspace.FilePath)
                && !_registered.Keys.Any(name => name.StartsWith(folder, StringComparison.OrdinalIgnoreCase)))
                return;

            var existing = registrar.Tasks(folder).ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
            _queried.Add(workspace.FilePath);
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in windows)
            {
                var name = host.TaskName(workspace.Id, entry);
                var task = existing.GetValueOrDefault(name);
                var status = OtherWorkspace(task, workspace, name) ?? Register(workspace, entry, trusted, exists: task is not null);
                _statuses[entry.Key] = status;
                if (status.State != WindowsTaskState.NotRegistered)
                    kept.Add(status.TaskName);
            }
            foreach (var (name, task) in existing)
            {
                if (!kept.Contains(name) && OtherWorkspace(task, workspace, name) is null)
                    TryDelete(name);
            }
            foreach (var name in _registered.Keys.Where(n => n.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && !kept.Contains(n)).ToList())
                _registered.Remove(name);
        }
    }

    /// <summary>Registers the fire after <paramref name="after"/>, or removes the task when the schedule may not have one.</summary>
    /// <param name="fromTask">The run was started by the task itself, so the task is this workspace's own.</param>
    public WindowsTaskStatus RegisterNext(LoadedWorkspace workspace, ScheduleEntry entry, bool trusted, DateTimeOffset after, bool fromTask)
    {
        lock (_lock)
        {
            var name = host.TaskName(workspace.Id, entry);
            if (!fromTask)
            {
                var task = registrar.Tasks(host.FolderOf(workspace.Id)).FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
                if (OtherWorkspace(task, workspace, name) is { } other)
                    return other;
            }
            var status = Register(workspace, entry, trusted, exists: false, after);
            if (status.State == WindowsTaskState.NotRegistered)
                TryDelete(status.TaskName);
            return status;
        }
    }

    public void Unregister(LoadedWorkspace workspace, ScheduleEntry entry)
    {
        lock (_lock)
            TryDelete(host.TaskName(workspace.Id, entry));
    }

    private WindowsTaskStatus? OtherWorkspace(RegisteredTask? task, LoadedWorkspace workspace, string name) =>
        task is not null && TaskSchedulerXml.OtherWorkspace(task.Arguments, workspace.FilePath) is { } other
            ? new WindowsTaskStatus(name, WindowsTaskState.OtherWorkspace, Reason: other) { At = _time.GetUtcNow() }
            : null;

    private WindowsTaskStatus Register(LoadedWorkspace workspace, ScheduleEntry entry, bool trusted, bool exists, DateTimeOffset? after = null)
    {
        var now = _time.GetUtcNow();
        var name = host.TaskName(workspace.Id, entry);
        var schedule = entry.Schedule;
        var reason = entry.Target is not { } target ? entry.Problem ?? $"'{schedule.Target}' was not found."
            : !trusted ? "Trust the workspace to run it in Windows."
            : !schedule.Enabled ? "Disabled"
            : !ScheduleGate.IsConfirmed(schedule, DefinitionHash.Of(target)) ? WaitingForConfirm
            : null;
        var next = reason is null ? TriggerMath.NextFire(schedule.Trigger, after ?? now, _zone) : null;
        if (next is null)
            return new WindowsTaskStatus(name, WindowsTaskState.NotRegistered, Reason: reason ?? "No next run") { At = now };

        var arguments = TaskSchedulerXml.Arguments(host, entry, workspace.FilePath, next.Value, _zone);
        if (arguments.Contains('%'))
            return new WindowsTaskStatus(name, WindowsTaskState.NotRegistered, Reason: ScheduleEntry.PercentInWindows) { At = now };
        var xml = TaskSchedulerXml.For(host, entry, workspace.FilePath, arguments, next.Value, _zone);
        if (exists && _registered.GetValueOrDefault(name) == xml)
            return new WindowsTaskStatus(name, WindowsTaskState.Registered, next) { At = now };
        try
        {
            registrar.Register(name, xml);
            _registered[name] = xml;
            return new WindowsTaskStatus(name, WindowsTaskState.Registered, next) { At = now };
        }
        catch (TaskRegistrarException ex)
        {
            _registered.Remove(name);
            return new WindowsTaskStatus(name, WindowsTaskState.Failed, Reason: ex.Message) { At = now };
        }
    }

    private void TryDelete(string name)
    {
        _registered.Remove(name);
        try
        {
            registrar.Delete(name);
        }
        catch (TaskRegistrarException)
        {
        }
    }
}
