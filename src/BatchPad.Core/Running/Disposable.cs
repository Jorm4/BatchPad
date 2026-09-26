namespace BatchPad.Core.Running;

public sealed class Disposable : IDisposable
{
    private Action? _dispose;

    private Disposable(Action? dispose) => _dispose = dispose;

    public static IDisposable None { get; } = new Disposable(null);

    /// <summary>Runs <paramref name="dispose"/> on the first <see cref="Dispose"/> only.</summary>
    public static IDisposable From(Action dispose) => new Disposable(dispose);

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
