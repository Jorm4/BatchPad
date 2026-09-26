using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class SettingsTests
{
    [TestMethod]
    public void TheToolbarOpensSettingsWithTheTelemetrySection()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();

        app.WaitFor(window, "SettingsButton").AsButton().Invoke();

        Assert.IsNotNull(app.WaitFor(window, "TelemetrySection"));
        Assert.IsNotNull(app.WaitFor(window, "TelemetryAddSink"));
        app.WaitFor(window, "SettingsCloseButton").AsButton().Invoke();
        Assert.IsNotNull(app.WaitFor(window, "SchedulesButton"));
    }
}
