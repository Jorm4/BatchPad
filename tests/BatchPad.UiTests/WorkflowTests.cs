using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class WorkflowTests
{
    [TestMethod]
    public void TheDemoWorkflowRunsInOneTabAndOpensInTheEditor()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        app.WaitFor(window, "Workspace/Build & run").AsTreeItem().Select();

        app.WaitFor(window, "RunButton").AsButton().Invoke();
        var steps = app.WaitFor(window, "WorkflowSteps");
        Assert.IsNotNull(app.WaitFor(steps, "Step_build"));
        Assert.IsNotNull(app.WaitFor(steps, "Step_run"));

        app.WaitFor(window, "EditButton").AsButton().Invoke();
        Assert.IsNotNull(app.WaitFor(window, "When_build"));
        Assert.IsNotNull(app.WaitFor(window, "ParamChip_game"));
        app.WaitFor(window, "WorkflowCancelButton").AsButton().Invoke();
    }
}
