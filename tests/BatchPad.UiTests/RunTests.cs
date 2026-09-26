using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class RunTests
{
    [TestMethod]
    public void HelloBatRunsAndShowsItsGreeting()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        app.WaitFor(window, "Workspace/Hello/hello.bat").AsTreeItem().Select();

        app.WaitFor(window, "RunButton").AsButton().Invoke();

        var status = app.WaitFor(window, "RunStatus");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (status.Name != "exit 0" && DateTime.UtcNow < deadline)
            Thread.Sleep(100);
        Assert.AreEqual("exit 0", status.Name);

        var lines = app.WaitFor(window, "OutputLines").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem));
        Assert.IsTrue(lines.Any(l => l.Name.StartsWith("Hello from batch!")),
            "output was: " + string.Join(" | ", lines.Select(l => l.Name)));
    }
}
