using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace BatchPad.UiTests;

public sealed class AppLauncher : IDisposable
{
    public Application Application { get; }
    public UIA3Automation Automation { get; } = new();

    public string DataDirectory { get; }

    private AppLauncher(Application application, string dataDirectory)
    {
        Application = application;
        DataDirectory = dataDirectory;
    }

    /// <summary>Starts the exe with a fresh data folder, so no test reads or changes the user's settings.</summary>
    public static AppLauncher Start(params string[] args)
    {
        var exe = LocateExe();
        var dataDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "BatchPadUiTests", Guid.NewGuid().ToString("N"))).FullName;
        var startInfo = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        startInfo.Environment["BATCHPAD_DATA_DIR"] = dataDirectory;
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        return new AppLauncher(Application.Launch(startInfo), dataDirectory);
    }

    public static string DemoWorkspace => Path.Combine(RepoRoot().FullName, "samples", "demo");

    public AutomationElement WaitFor(AutomationElement parent, string automationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var element = parent.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (element is not null)
                return element;
            if (DateTime.UtcNow > deadline)
                throw new AssertFailedException($"No element with automation id '{automationId}'. Found: "
                    + string.Join(", ", parent.FindAllDescendants().Select(e => e.Properties.AutomationId.ValueOrDefault).Where(id => !string.IsNullOrEmpty(id)).Distinct()));
            Thread.Sleep(100);
        }
    }

    public static T WaitUntil<T>(Func<T?> probe, string what, TimeSpan? timeout = null) where T : class
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            if (probe() is { } found)
                return found;
            if (DateTime.UtcNow > deadline)
                throw new AssertFailedException($"Timed out waiting for {what}.");
            Thread.Sleep(100);
        }
    }

    public Window MainWindow(TimeSpan? timeout = null) =>
        Application.GetMainWindow(Automation, timeout ?? TimeSpan.FromSeconds(20))
        ?? throw new AssertFailedException("BatchPad main window did not appear.");

    public bool WaitForExit(TimeSpan timeout)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(Application.ProcessId);
            return process.WaitForExit(timeout);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    public static string LocateExe()
    {
        var overridePath = Environment.GetEnvironmentVariable("BATCHPAD_EXE");
        if (!string.IsNullOrEmpty(overridePath))
            return Path.GetFullPath(overridePath);

        // Test output is tests/BatchPad.UiTests/bin/<config>/<tfm>/; the app builds to the matching config.
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = tfmDir.Parent!.Name;
        var exe = Path.Combine(RepoRoot().FullName, "src", "BatchPad.App", "bin", configuration, tfmDir.Name, "BatchPad.exe");
        return File.Exists(exe) ? exe : throw new FileNotFoundException("Built BatchPad.exe not found; set BATCHPAD_EXE.", exe);
    }

    private static DirectoryInfo RepoRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "BatchPad.slnx")))
            root = root.Parent ?? throw new FileNotFoundException("Repo root (BatchPad.slnx) not found.");
        return root;
    }

    public void Dispose()
    {
        try
        {
            Application.Close();
            if (!Application.HasExited)
                Application.Kill();
        }
        finally
        {
            Automation.Dispose();
            Application.Dispose();
            try
            {
                Directory.Delete(DataDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
