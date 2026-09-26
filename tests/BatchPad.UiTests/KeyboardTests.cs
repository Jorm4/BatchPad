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
    public void EnterRunsTheSelectedScriptAndCtrlKFocusesSearch()
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

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);

        var filter = app.WaitFor(window, "FilterBox");
        AppLauncher.WaitUntil(() => filter.Properties.HasKeyboardFocus.ValueOrDefault ? filter : null, "the search box to take focus");
    }
}
