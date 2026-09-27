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

    private readonly TrayService _tray = new();
    private MainViewModel? _main;
    private EventWaitHandle? _exitRequest;
    private InstanceChannelSwitch? _channel;
    private bool _showingError;

    /// <summary>Setting this event exits BatchPad as the tray's Exit does, even while hidden to the tray; tools/update-stable.ps1 uses it.</summary>
    public static string ExitEventName(int processId) => $"BatchPad-Exit-{processId}";

    protected override void OnExit(ExitEventArgs e)
    {
        _tray.Dispose();
        _exitRequest?.Dispose();
        _channel?.Dispose();
        _main?.Shutdown();
        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var paths = ResolvePaths();
        var log = new ErrorLog(Path.Combine(paths.LocalDirectory, "errors.log"));
        CatchUnexpectedErrors(log);
        var main = _main = new MainViewModel(paths, LoadSettings(paths), dispatcher: new WpfDispatcher(Dispatcher), tray: _tray)
        {
            JumpList = new JumpListService(),
        };
        var arguments = GuiArguments.Parse(e.Args);
        main.OpenInitial(arguments.Workspace, Environment.CurrentDirectory, AppContext.BaseDirectory);
        main.EnableFileWatching();
        new MainWindow { DataContext = main }.Show();
        var channel = _channel = new InstanceChannelSwitch(Environment.ProcessPath!, () => main.Workspace?.FilePath,
            action => Dispatcher.BeginInvoke(action), key =>
            {
                main.ShowWindow();
                main.RunByKey(key);
            }, log.Append);
        channel.Update();
        main.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainViewModel.Workspace))
                channel.Update();
        };
        if (arguments.RunKey is { } runKey)
            Dispatcher.BeginInvoke(() => main.RunByKey(runKey));
        _exitRequest = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName(Environment.ProcessId));
        ThreadPool.RegisterWaitForSingleObject(_exitRequest, (_, _) => Dispatcher.BeginInvoke(main.Exit), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Sends <c>--run</c> to the instance that has the workspace open; false when none answers.</summary>
    internal static bool HandOff(string? workspace, string runKey)
    {
        var paths = ResolvePaths();
        var file = WorkspaceLocator.Locate(workspace, Environment.CurrentDirectory, LoadSettings(paths, TextWriter.Null).RecentWorkspaces,
            AppContext.BaseDirectory);
        if (file is null)
            return false;
        // The receiving instance may bring its window forward only if this process, which the user just started, allows it.
        AllowSetForegroundWindow(AnyProcess);
        return InstanceChannel.TrySend(InstanceChannel.PipeNameFor(Environment.ProcessPath!, file), runKey);
    }

    /// <summary>Logs an error nothing else handled and, on the UI thread, reports it and keeps BatchPad running.</summary>
    private void CatchUnexpectedErrors(ErrorLog log)
    {
        DispatcherUnhandledException += (_, e) =>
        {
            log.Append(e.Exception);
            e.Handled = true;
            // One at a time: an error raised on every layout pass would otherwise stack up message boxes.
            if (_showingError)
                return;
            _showingError = true;
            try
            {
                MessageBox.Show($"Something went wrong, and BatchPad kept running.\n\n{e.Exception.Message}\n\nDetails are in {log.Path}.",
                    "BatchPad", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _showingError = false;
            }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Append(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Append(e.Exception);
            e.SetObserved();
        };
    }

    internal static int RunCli(string[] args)
    {
        AttachParentConsole();
        var output = Console.IsOutputRedirected ? Utf8Writer(Console.OpenStandardOutput()) : Console.Out;
        var error = Console.IsErrorRedirected ? Utf8Writer(Console.OpenStandardError()) : Console.Error;
        var paths = ResolvePaths();
        var settings = LoadSettings(paths, error);
        return Task.Run(() => new CliRunner(paths, settings, output, error).RunAsync(args, Environment.CurrentDirectory))
            .GetAwaiter().GetResult();
    }

    internal static void PrintVersion()
    {
        AttachParentConsole();
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Console.WriteLine($"BatchPad {version}");
    }

    private static StreamWriter Utf8Writer(Stream stream) => new(stream, new UTF8Encoding(false)) { AutoFlush = true };

    /// <summary>A WinExe has no console of its own; borrow the caller's so output shows in cmd too.</summary>
    private static void AttachParentConsole()
    {
        if (!Console.IsOutputRedirected)
            AttachConsole(AttachParentProcess);
    }

    private const int AttachParentProcess = -1;
    private const int AnyProcess = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    private static AppPaths ResolvePaths() =>
        Environment.GetEnvironmentVariable(DataDirectoryVariable) is { Length: > 0 } dataDirectory
            ? new AppPaths(Path.GetFullPath(dataDirectory))
            : AppPaths.ForCurrentProcess();

    public static Settings LoadSettings(AppPaths paths, TextWriter? errors = null)
    {
        try
        {
            return Settings.Load(paths.SettingsFile);
        }
        catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
        {
            if (errors is not null)
                errors.WriteLine(ex.Message);
            else
                MessageBox.Show(ex.Message, "BatchPad settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return new Settings();
        }
    }
}
