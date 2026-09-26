#:package FlaUI.UIA3@5.0.0
#:property TargetFramework=net10.0-windows
#:property ManagePackageVersionsCentrally=false
#:property PublishAot=false

// Regenerates docs/images/main-window.png from the demo workspace.
// Usage (from the repo root, after tools\publish.bat): dotnet run tools/screenshot.cs [path\to\BatchPad.exe]

using System.Diagnostics;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

var repo = Directory.GetCurrentDirectory();
var exe = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine("dist", "BatchPad.exe"));
var output = Path.Combine(repo, "docs", "images", "main-window.png");
var dataDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BatchPadScreenshot", Guid.NewGuid().ToString("N"))).FullName;

var startInfo = new ProcessStartInfo(exe) { UseShellExecute = false };
startInfo.Environment["BATCHPAD_DATA_DIR"] = dataDirectory;
startInfo.ArgumentList.Add(Path.Combine(repo, "samples", "demo"));

// Without this, capture coordinates and screen pixels disagree on scaled displays.
SetProcessDpiAwarenessContext(-4);

using var automation = new UIA3Automation();
var app = Application.Launch(startInfo);
try
{
    var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20))!;
    var scale = GetDpiForWindow(window.Properties.NativeWindowHandle.Value) / 96.0;
    window.Patterns.Transform.Pattern.Move(80, 60);
    window.Patterns.Transform.Pattern.Resize(1280 * scale, 960 * scale);

    WaitFor(window, "TrustButton").AsButton().Invoke();

    Run(window, "Workspace/Hello/hello.bat");

    WaitFor(window, "Workspace/Parameters demo").AsTreeItem().Select();
    WaitFor(window, "Param_config").FindFirstChild(cf => cf.ByName("Release"))!.AsListBoxItem().Select();
    var tests = WaitFor(window, "Param_tests");
    foreach (var name in new[] { "Physics", "Audio" })
        tests.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox).And(cf.ByName(name)))!.AsCheckBox().IsChecked = true;
    WaitFor(window, "Param_verbose").AsCheckBox().IsChecked = true;
    Run(window, null);

    var scroller = WaitFor(window, "Param_config");
    while (scroller.Parent is { } parent && !(scroller.Patterns.Scroll.IsSupported && scroller.Patterns.Scroll.Pattern.VerticallyScrollable))
        scroller = parent;
    scroller.Patterns.Scroll.Pattern.SetScrollPercent(-1, 0);

    window.SetForeground();
    Thread.Sleep(500);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    // The window rectangle includes Windows 11's invisible resize borders; the DWM frame is what's on screen.
    DwmGetWindowAttribute(window.Properties.NativeWindowHandle.Value, 9, out var frame, Marshal.SizeOf<Rect>());
    Capture.Rectangle(new System.Drawing.Rectangle(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top)).ToFile(output);
    Console.WriteLine($"Wrote {output}");
}
finally
{
    app.Close();
    if (!app.HasExited && !Process.GetProcessById(app.ProcessId).WaitForExit(5000))
        app.Kill();
    try { Directory.Delete(dataDirectory, recursive: true); } catch (IOException) { }
}

void Run(Window window, string? treeItem)
{
    if (treeItem is not null)
        WaitFor(window, treeItem).AsTreeItem().Select();
    var runsBefore = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).Length;
    WaitFor(window, "RunButton").AsButton().Invoke();
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (DateTime.UtcNow < deadline)
    {
        var newTabOpened = window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).Length > runsBefore;
        if (newTabOpened && window.FindFirstDescendant(cf => cf.ByAutomationId("RunStatus"))?.Name == "exit 0")
            return;
        Thread.Sleep(100);
    }
    throw new TimeoutException("Run did not finish with exit 0.");
}

[DllImport("user32.dll")]
static extern bool SetProcessDpiAwarenessContext(nint value);

[DllImport("user32.dll")]
static extern uint GetDpiForWindow(nint hwnd);

[DllImport("dwmapi.dll")]
static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);


AutomationElement WaitFor(AutomationElement parent, string automationId)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (DateTime.UtcNow < deadline)
    {
        if (parent.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } element)
            return element;
        Thread.Sleep(100);
    }
    throw new TimeoutException($"No element with automation id '{automationId}'.");
}

struct Rect { public int Left, Top, Right, Bottom; }
