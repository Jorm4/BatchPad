using System.Diagnostics;
using System.Windows;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;

namespace BatchPad.App.Services;

public interface IShellService
{
    void CopyText(string text);

    /// <summary>Opens what <see cref="LinkPolicy.MayOpen"/> allows and ignores anything else.</summary>
    void Open(string target, bool confirmed = false);

    void RunCommand(string commandLine, IReadOnlyDictionary<string, string>? environment = null);
}

public sealed class ShellService : IShellService
{
    public void CopyText(string text) => Clipboard.SetText(text);

    public void Open(string target, bool confirmed = false)
    {
        if (LinkPolicy.MayOpen(target, confirmed))
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    public void RunCommand(string commandLine, IReadOnlyDictionary<string, string>? environment = null) =>
        Process.Start(CommandStartInfo(commandLine, environment))?.Dispose();

    /// <summary>Runs through <c>cmd /v:on</c>, so <c>!NAME!</c> reads a value from <paramref name="environment"/> with no special characters.</summary>
    public static ProcessStartInfo CommandStartInfo(string commandLine, IReadOnlyDictionary<string, string>? environment)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/d /v:on /s /c \"{commandLine}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        start.Environment["NoDefaultCurrentDirectoryInExePath"] = "1";
        foreach (var (name, value) in environment ?? new Dictionary<string, string>())
            start.Environment[name] = value;
        return start;
    }
}

public sealed class ShellOpener(IShellService shell) : IShellOpener
{
    public void Open(string target) => shell.Open(target);
}
