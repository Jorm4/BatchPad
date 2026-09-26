using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class SchedulesTests
{
    [TestMethod]
    public void AnEveryMinuteScheduleRunsNowAndShowsItsResult()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        app.WaitFor(window, "Workspace/Hello/hello.bat").AsTreeItem().Select();

        app.WaitFor(window, "SchedulesButton").AsButton().Invoke();
        app.WaitFor(window, "AddScheduleButton").AsButton().Invoke();
        var kinds = app.WaitFor(window, "ScheduleKinds");
        AppLauncher.WaitUntil(() => kinds.FindFirstChild(cf => cf.ByName("Every")), "the Every kind").AsListBoxItem().Select();
        app.WaitFor(window, "ScheduleEvery").AsTextBox().Text = "1m";
        app.WaitFor(window, "ScheduleSaveButton").AsButton().Invoke();

        Assert.AreEqual("Every 1 minute", app.WaitFor(window, "ScheduleTrigger").Name);
        app.WaitFor(window, "ScheduleRunNow").AsButton().Invoke();

        var result = app.WaitFor(window, "ScheduleLastResult");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (result.Name != "exit 0" && DateTime.UtcNow < deadline)
            Thread.Sleep(100);
        Assert.AreEqual("exit 0", result.Name);
        Assert.IsNotNull(app.WaitFor(window, "ScheduleBadge"));
    }
}
