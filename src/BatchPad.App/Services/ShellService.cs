using System.Diagnostics;
using System.Windows;
using BatchPad.Core.Running;

namespace BatchPad.App.Services;

public interface IShellService
{
    void CopyText(string text);
    void Open(string target);
}

public sealed class ShellService : IShellService
{
    public void CopyText(string text) => Clipboard.SetText(text);

    public void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}

public sealed class ShellOpener(IShellService shell) : IShellOpener
{
    public void Open(string target) => shell.Open(target);
}
