using System.Windows;
using BatchPad.App.Services;

namespace BatchPad.App.Views;

internal sealed class WindowActivity : IWindowActivity
{
    private readonly Window _window;

    public WindowActivity(Window window)
    {
        _window = window;
        window.Activated += (_, _) => Activated?.Invoke();
    }

    public bool IsActive => _window.IsActive;

    public event Action? Activated;
}
