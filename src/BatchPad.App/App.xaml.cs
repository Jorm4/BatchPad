using System.Reflection;
using System.Runtime.InteropServices;

using System.Text;
using System.Windows;
using BatchPad.App.Cli;

using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.App.Views;
using BatchPad.Core.Config;
using BatchPad.Core.Workspace;

namespace BatchPad.App;

public partial class App : Application
{
    /// <summary>Overrides the data folder, so UI tests never touch the user's settings.</summary>
    public const string DataDirectoryVariable = "BATCHPAD_DATA_DIR";

    private TrayService? _tray;

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--version"])
        {
            PrintVersion();
            Shutdown(0);
            return;
        }
        var paths = ResolvePaths();
        if (CliCommand.IsCli(e.Args))
        {
            AttachParentConsole();
            var output = Console.IsOutputRedirected ? Utf8Writer(Console.OpenStandardOutput()) : Console.Out;
            var error = Console.IsErrorRedirected ? Utf8Writer(Console.OpenStandardError()) : Console.Error;
            var settings = LoadSettings(paths, error);
            var code = Task.Run(() => new CliRunner(paths, settings, output, error).RunAsync(e.Args, Environment.CurrentDirectory))
                .GetAwaiter().GetResult();
            output.Flush();
            error.Flush();
            Shutdown(code);
            return;
        }
        _tray = new TrayService();
        var main = new MainViewModel(paths, LoadSettings(paths), dispatcher: new WpfDispatcher(Dispatcher), tray: _tray);
        main.OpenInitial(e.Args.FirstOrDefault(), Environment.CurrentDirectory);
        main.EnableFileWatching();
        new MainWindow { DataContext = main }.Show();
    }

    private static StreamWriter Utf8Writer(Stream stream) => new(stream, new UTF8Encoding(false)) { AutoFlush = true };

    private static void PrintVersion()
    {
        AttachParentConsole();
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine($"BatchPad {version}");
    }

    /// <summary>A WinExe has no console of its own; borrow the caller's so output shows in cmd too.</summary>
    private static void AttachParentConsole()
    {
        if (!Console.IsOutputRedirected)
            AttachConsole(AttachParentProcess);
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static AppPaths ResolvePaths() =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } dataDirectory
            ? new AppPaths(Path.GetFullPath(dataDirectory), isPortable: true)
            : AppPaths.ForCurrentProcess();

    private static Settings LoadSettings(AppPaths paths, TextWriter? errors = null)
    {
        try
        {
            return Settings.Load(paths.SettingsFile);
        }
        catch (ConfigException ex)
        {
            if (errors is not null)
                errors.WriteLine(ex.Message);
            else
                MessageBox.Show(ex.Message, "BatchPad settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new Settings();
        }
    }
}
