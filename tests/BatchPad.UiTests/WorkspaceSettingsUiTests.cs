using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class WorkspaceSettingsUiTests
{
    [TestMethod]
    public void TheWorkspacePageShowsSettingsAndCancelReturnsToDetails()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();

        app.WaitFor(window, "WorkspaceButton").AsButton().Invoke();

        Assert.AreEqual("Demo", app.WaitFor(window, "WorkspaceName").AsTextBox().Text);
        Assert.IsNotNull(app.WaitFor(window, "ScriptFolderPath"));
        Assert.IsNotNull(app.WaitFor(window, "AddSharedParam"));
        app.WaitFor(window, "WorkspaceCancelButton").AsButton().Invoke();
        app.WaitFor(window, "Workspace/Hello/hello.bat").AsTreeItem().Select();
        Assert.IsNotNull(app.WaitFor(window, "RunButton"));
    }
}
