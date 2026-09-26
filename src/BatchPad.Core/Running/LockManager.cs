namespace BatchPad.Core.Running;

/// <summary>
/// Named locks (§4 "Locks and concurrency"): waiters queue first-in first-out, and a lock is re-entrant for the
/// owner token that holds it, so a workflow holding a lock can run a step that takes it too.
/// </summary>
public sealed class LockManager
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Holder> _holders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Takes every lock in <paramref name="names"/>, in a fixed order so two runs can't deadlock over two locks.</summary>
    /// <param name="waiting">Called with a lock's name whenever the caller has to queue for it.</param>
    public Task<LockLease> AcquireAsync(IEnumerable<string> names, object owner, Action<string>? waiting = null,
        CancellationToken cancellation = default) =>
        AcquireAsync(names.Select(name => (name, owner)), waiting, cancellation);

    /// <summary>Takes each lock for its own owner, in the same fixed order.</summary>
    public async Task<LockLease> AcquireAsync(IEnumerable<(string Name, object Owner)> locks, Action<string>? waiting = null,
        CancellationToken cancellation = default)
    {
        var lease = new LockLease(this);
        try
        {
            foreach (var (name, owner) in locks.DistinctBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
            {
                await AcquireOneAsync(name, owner, waiting, cancellation);
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

    public bool IsHeld(string name)
    {
        lock (_lock)
            return _holders.ContainsKey(name);
    }

    private Task AcquireOneAsync(string name, object owner, Action<string>? waiting, CancellationToken cancellation)
    {
        TaskCompletionSource waiter;
        lock (_lock)
        {
            if (!_holders.TryGetValue(name, out var holder))
            {
                _holders[name] = new Holder(owner);
                return Task.CompletedTask;
            }
            if (ReferenceEquals(holder.Owner, owner))
            {
                holder.Count++;
                return Task.CompletedTask;
            }
            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            holder.Queue.AddLast((owner, waiter));
        }
        waiting?.Invoke(name);
        return WaitAsync(name, owner, waiter, cancellation);
    }

    private async Task WaitAsync(string name, object owner, TaskCompletionSource waiter, CancellationToken cancellation)
    {
        await using var registration = cancellation.Register(() =>
        {
            lock (_lock)
            {
                if (_holders.TryGetValue(name, out var holder) && holder.Queue.Remove((owner, waiter)))
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
            if (holder.Queue.First is not { } next)
            {
                _holders.Remove(name);
                return;
            }
            holder.Queue.RemoveFirst();
            (holder.Owner, holder.Count) = (next.Value.Owner, 1);
            next.Value.Waiter.SetResult();
        }
    }

    private sealed class Holder(object owner)
    {
        public object Owner { get; set; } = owner;
        public int Count { get; set; } = 1;
        public LinkedList<(object Owner, TaskCompletionSource Waiter)> Queue { get; } = [];
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
