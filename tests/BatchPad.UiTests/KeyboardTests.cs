using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class KeyboardTests
{
    [TestMethod]
    public void EnterRunsTheSelectedScript()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        var hello = app.WaitFor(window, "Workspace/Hello/hello.bat").AsTreeItem();
        hello.Select();
        hello.Focus();

        Keyboard.Type(VirtualKeyShort.RETURN);

        var tabs = app.WaitFor(window, "OutputTabs");
        AppLauncher.WaitUntil(() => tabs.FindFirstChild(cf => cf.ByControlType(ControlType.TabItem)), "a new output tab");
    }

    [TestMethod]
    public void CtrlKOpensThePaletteAndEnterRunsTheMatch()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        app.WaitFor(window, "Workspace/Hello/hello.bat");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        var query = app.WaitFor(window, "PaletteQuery");
        AppLauncher.WaitUntil(() => query.Properties.HasKeyboardFocus.ValueOrDefault ? query : null, "the palette to take focus");
        Keyboard.Type("hello.bat");
        app.WaitFor(window, "PaletteResults");
        Keyboard.Type(VirtualKeyShort.RETURN);

        var tabs = app.WaitFor(window, "OutputTabs");
        AppLauncher.WaitUntil(() => tabs.FindFirstChild(cf => cf.ByControlType(ControlType.TabItem)), "a new output tab");
    }
}
