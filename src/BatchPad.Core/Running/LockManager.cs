using System.Globalization;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>
/// Named locks (§4 "Locks and concurrency"): waiters queue first-in first-out, and a lock is re-entrant for the
/// owner token that holds it, so a workflow holding a lock can run a step that takes it too. Given a directory, a
/// held lock also excludes other processes using that directory (§4.5).
/// </summary>
public sealed class LockManager(string? machineDirectory = null)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Holder> _holders = new(StringComparer.OrdinalIgnoreCase);

    public static LockManager For(AppPaths paths) => new(Path.Combine(paths.LocalDirectory, "locks"));

    /// <summary>Where the lock files live; null when locks exclude only within this process.</summary>
    public string? MachineDirectory { get; } = machineDirectory;

    /// <summary>Takes every lock in <paramref name="names"/>, in a fixed order so two runs can't deadlock over two locks.</summary>
    /// <param name="waiting">Called with what the caller is queued behind: the lock's name, and the holder when that is another process.</param>
    /// <param name="wait">False to fail with <see cref="LockBusyException"/> instead of queueing.</param>
    /// <param name="holder">Names the holder to other processes waiting for the lock.</param>
    public Task<LockLease> AcquireAsync(IEnumerable<string> names, object owner, Action<string>? waiting = null,
        CancellationToken cancellation = default, bool wait = true, string? holder = null) =>
        AcquireAsync(names.Select(name => (name, owner)), waiting, cancellation, wait, holder);

    /// <summary>Takes each lock for its own owner, in the same fixed order.</summary>
    public async Task<LockLease> AcquireAsync(IEnumerable<(string Name, object Owner)> locks, Action<string>? waiting = null,
        CancellationToken cancellation = default, bool wait = true, string? holder = null)
    {
        var lease = new LockLease(this);
        try
        {
            foreach (var (name, owner) in locks.DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                await AcquireOneAsync(new Request(name, owner, holder, waiting, wait), cancellation);
                lease.Held.Add((name, owner));
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
        return lease;
    }

    /// <summary>Who holds <paramref name="name"/>, here or in another process, as "held by Build since 09:12"; null when nobody does.</summary>
    public string? DescribeHolder(string name)
    {
        lock (_lock)
        {
            if (_holders.TryGetValue(name, out var holder) && holder.Machine is { IsCompletedSuccessfully: true })
                return holder.Description;
        }
        if (MachineDirectory is null)
            return null;
        using var probe = MachineLock.TryAcquire(MachineDirectory, name, null);
        return probe is null ? MachineLock.DescribeHolder(MachineDirectory, name) : null;
    }

    internal bool IsHeld(string name)
    {
        lock (_lock)
            return _holders.ContainsKey(name);
    }

    private async Task AcquireOneAsync(Request request, CancellationToken cancellation)
    {
        TaskCompletionSource? waiter = null;
        Task? machine = null;
        lock (_lock)
        {
            if (!_holders.TryGetValue(request.Name, out var holder))
                _holders[request.Name] = holder = new Holder(request.Owner, request.HolderName);
            else if (ReferenceEquals(holder.Owner, request.Owner))
                holder.Count++;
            else if (!request.Wait)
                throw new LockBusyException(request.Name, holder.Description);
            else
            {
                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                holder.Queue.AddLast((request.Owner, request.HolderName, waiter));
            }
            if (waiter is null)
                machine = MachineFor(holder, request);
        }
        if (waiter is not null)
        {
            request.Waiting?.Invoke(request.Name);
            await WaitAsync(request, waiter, cancellation);
            lock (_lock)
                machine = MachineFor(_holders[request.Name], request);
        }
        if (machine!.IsCompletedSuccessfully)
            return;
        try
        {
            if (!request.Wait && !machine.IsCompleted)
                throw new LockBusyException(request.Name, "being taken by another run");
            await machine.WaitAsync(cancellation);
        }
        catch
        {
            Release(request.Name, request.Owner);
            throw;
        }
    }

    private Task MachineFor(Holder holder, Request request)
    {
        if (holder.Machine is not null)
            return holder.Machine;
        if (MachineDirectory is null)
            return holder.Machine = Task.FromResult<MachineLock?>(null);
        holder.MachineCancel = new CancellationTokenSource();
        return holder.Machine = MachineLock.AcquireAsync(MachineDirectory, request.Name, request.HolderName, request.Waiting,
            request.Wait, holder.MachineCancel.Token);
    }

    private async Task WaitAsync(Request request, TaskCompletionSource waiter, CancellationToken cancellation)
    {
        await using var registration = cancellation.Register(() =>
        {
            lock (_lock)
            {
                if (_holders.TryGetValue(request.Name, out var holder) && holder.Queue.Remove((request.Owner, request.HolderName, waiter)))
                    waiter.TrySetCanceled(cancellation);
            }
        });
        await waiter.Task;
    }

    internal void Release(string name, object owner)
    {
        lock (_lock)
        {
            if (!_holders.TryGetValue(name, out var holder) || !ReferenceEquals(holder.Owner, owner) || --holder.Count > 0)
                return;
            var next = holder.Queue.First;
            if (next is null || holder.Machine is not { IsCompletedSuccessfully: true })
                ReleaseMachine(holder);
            if (next is null)
            {
                _holders.Remove(name);
                return;
            }
            holder.Queue.RemoveFirst();
            (holder.Owner, holder.HolderName, holder.Count, holder.Since) = (next.Value.Owner, next.Value.HolderName, 1, DateTimeOffset.Now);
            holder.Machine?.Result?.Hand(holder.HolderName);
            next.Value.Waiter.SetResult();
        }
    }

    private static void ReleaseMachine(Holder holder)
    {
        if (holder.Machine is not { } machine)
            return;
        if (machine.IsCompletedSuccessfully)
            machine.Result?.Dispose();
        else
        {
            holder.MachineCancel?.Cancel();
            machine.ContinueWith(static t =>
            {
                if (t.IsCompletedSuccessfully)
                    t.Result?.Dispose();
            }, TaskScheduler.Default);
        }
        holder.MachineCancel?.Dispose();
        (holder.Machine, holder.MachineCancel) = (null, null);
    }

    private sealed record Request(string Name, object Owner, string? HolderName, Action<string>? Waiting, bool Wait);

    private sealed class Holder(object owner, string? holderName)
    {
        public object Owner { get; set; } = owner;
        public string? HolderName { get; set; } = holderName;
        public DateTimeOffset Since { get; set; } = DateTimeOffset.Now;
        public int Count { get; set; } = 1;
        public Task<MachineLock?>? Machine { get; set; }
        public CancellationTokenSource? MachineCancel { get; set; }
        public LinkedList<(object Owner, string? HolderName, TaskCompletionSource Waiter)> Queue { get; } = [];
        public string Description => $"held by {HolderName ?? "another run"} since {Since.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}

/// <summary>Held locks; disposing releases them once.</summary>
public sealed class LockLease : IDisposable
{
    private readonly LockManager _manager;
    private int _released;

    internal LockLease(LockManager manager) => _manager = manager;

    internal List<(string Name, object Owner)> Held { get; } = [];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            foreach (var (name, owner) in Held)
                _manager.Release(name, owner);
    }
}
