using System.Windows.Threading;

namespace BatchPad.App.Services;

public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread without waiting for it.</summary>
    void Post(Action action);
}

public sealed class WpfDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action) => dispatcher.BeginInvoke(action);
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
