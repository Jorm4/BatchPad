using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class InsightsTests
{
    [TestMethod]
    public void TheToolbarOpensInsightsWithTheScriptTable()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();

        app.WaitFor(window, "InsightsButton").AsButton().Invoke();

        Assert.IsNotNull(app.WaitFor(window, "InsightsScripts"));
        Assert.IsNotNull(app.WaitFor(window, "InsightsSortMedian"));
        app.WaitFor(window, "InsightsCloseButton").AsButton().Invoke();
        Assert.IsNotNull(app.WaitFor(window, "SchedulesButton"));
    }
}
