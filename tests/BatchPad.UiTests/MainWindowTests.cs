using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class MainWindowTests
{
    [TestMethod]
    public void DemoTreeShowsScriptsAfterTrusting()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();

        app.WaitFor(window, "TrustButton").AsButton().Invoke();

        Assert.IsNotNull(app.WaitFor(window, "Workspace/Hello/hello.bat"));
        Assert.IsNotNull(app.WaitFor(window, "Workspace/Hello/hello.py"));
        app.WaitFor(window, "Workspace/Parameters demo").AsTreeItem().Select();
        Assert.IsNotNull(app.WaitFor(window, "Param_config"));
        Assert.IsNotNull(app.WaitFor(window, "Param_port"));
        Assert.IsNotNull(app.WaitFor(window, "Param_tests_Summary"));
        StringAssert.Contains(File.ReadAllText(Path.Combine(app.DataDirectory, "settings.json")), "trustedFolders");
    }
}
