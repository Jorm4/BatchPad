using System.Windows.Threading;

namespace BatchPad.App.Services;

public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting for it.</summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="work"/> off the UI thread after <paramref name="delay"/>, then <paramref name="apply"/> on it.</summary>
    void Background<T>(Func<T> work, Action<T> apply, TimeSpan delay = default);
}

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public void Background<T>(Func<T> work, Action<T> apply, TimeSpan delay = default) =>
        Task.Delay(delay).ContinueWith(_ => work(), TaskScheduler.Default)
            .ContinueWith(done => Post(() => apply(done.Result)), TaskScheduler.Default);
}

/// <summary>Runs everything inline, background work included, so tests stay deterministic.</summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();

    public void Background<T>(Func<T> work, Action<T> apply, TimeSpan delay = default) => apply(work());
}

/// <summary>Background work where only the latest request counts: an older one is skipped or its result dropped.</summary>
public sealed class Debouncer(IUiDispatcher dispatcher, TimeSpan delay)
{
    private int _latest;

    public void Run<T>(Func<T> work, Action<T> apply)
    {
        var request = Interlocked.Increment(ref _latest);
        dispatcher.Background(
            () => IsLatest(request) ? (true, work()) : (false, default(T)!),
            result =>
            {
                if (result.Item1 && IsLatest(request))
                    apply(result.Item2);
            },
            delay);
    }

    /// <summary>Drops work already under way, so its result never applies.</summary>
    public void Cancel() => Interlocked.Increment(ref _latest);

    private bool IsLatest(int request) => Volatile.Read(ref _latest) == request;
}
