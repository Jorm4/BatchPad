using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
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
        var main = new MainViewModel(paths, LoadSettings(paths), dispatcher: new WpfDispatcher(Dispatcher));
        main.OpenInitial(e.Args.FirstOrDefault(), Environment.CurrentDirectory);
        main.EnableFileWatching();
        new MainWindow { DataContext = main }.Show();
    }

    private static void PrintVersion()
    {
        // A WinExe has no console of its own; borrow the caller's so the version shows in cmd too.
        if (!Console.IsOutputRedirected)
            AttachConsole(AttachParentProcess);
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine($"BatchPad {version}");
    }

    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static AppPaths ResolvePaths() =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } dataDirectory
            ? new AppPaths(Path.GetFullPath(dataDirectory), isPortable: true)
            : AppPaths.ForCurrentProcess();

    private static Settings LoadSettings(AppPaths paths)
    {
        try
        {
            return Settings.Load(paths.SettingsFile);
        }
        catch (ConfigException ex)
        {
            MessageBox.Show(ex.Message, "BatchPad settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new Settings();
        }
    }
}
