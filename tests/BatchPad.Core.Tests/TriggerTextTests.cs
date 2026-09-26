using BatchPad.Core.Model;
using BatchPad.Core.Scheduling;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TriggerTextTests
{
    [TestMethod]
    [DataRow("0 2 * * 1-5", "Weekdays at 02:00")]
    [DataRow("30 3 * * *", "Daily at 03:30")]
    [DataRow("0 9 * * 1", "Mondays at 09:00")]
    [DataRow("*/15 * * * *", "Every 15 minutes")]
    [DataRow("5 * * * *", "Hourly at :05")]
    [DataRow("0 6 1 * *", "Monthly on day 1 at 06:00")]
    [DataRow("0 6 1 jan *", "cron 0 6 1 jan *")]
    public void CronReadsAsWords(string cron, string expected) =>
        Assert.AreEqual(expected, TriggerText.Describe(new Trigger { Cron = cron }));

    [TestMethod]
    public void OtherKindsReadAsWords()
    {
        Assert.AreEqual("Every 1 hour 30 minutes, 09:00–18:00", TriggerText.Describe(new Trigger { Every = "1h30m", Between = "09:00-18:00" }));
        Assert.AreEqual("When assets/**/*.png changes (after 5 seconds quiet)",
            TriggerText.Describe(new Trigger { FileChanged = "assets/**/*.png", Debounce = "5s" }));
        Assert.AreEqual("After workspace:build fails", TriggerText.Describe(new Trigger { AfterRun = "workspace:build", Result = AfterRunResult.Failure }));
        Assert.AreEqual("When the workspace opens", TriggerText.Describe(new Trigger { OnStart = true }));
    }
}
