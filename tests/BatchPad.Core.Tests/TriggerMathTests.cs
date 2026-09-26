using BatchPad.Core.Model;
using BatchPad.Core.Scheduling;
using static BatchPad.Core.Tests.CronTests;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TriggerMathTests
{
    private static DateTimeOffset? Next(Trigger trigger, DateTimeOffset after) => TriggerMath.NextFire(trigger, after, Berlin);

    [TestMethod]
    public void EveryWithAWindowSkipsTheNightAndResumesAtItsStart()
    {
        var trigger = new Trigger { Every = "30m", Between = "09:00-18:00" };

        Assert.AreEqual(Local(2026, 9, 25, 9, 30), Next(trigger, Local(2026, 9, 25, 9, 10)));
        Assert.AreEqual(Local(2026, 9, 25, 18, 0), Next(trigger, Local(2026, 9, 25, 17, 30)));
        Assert.AreEqual(Local(2026, 9, 26, 9, 0), Next(trigger, Local(2026, 9, 25, 18, 0)));
        Assert.AreEqual(Local(2026, 9, 26, 9, 0), Next(trigger, Local(2026, 9, 26, 3, 0)));
    }

    [TestMethod]
    public void EveryWithoutAWindowCountsFromMidnight()
    {
        Assert.AreEqual(Local(2026, 9, 25, 14, 0), Next(new Trigger { Every = "2h" }, Local(2026, 9, 25, 13, 7)));
        Assert.AreEqual(Local(2026, 9, 26, 0, 0), Next(new Trigger { Every = "1h" }, Local(2026, 9, 25, 23, 0)));
    }

    [TestMethod]
    public void AWindowCanWrapPastMidnight()
    {
        var trigger = new Trigger { Every = "1h", Between = "22:00-02:00" };

        Assert.AreEqual(Local(2026, 9, 26, 1, 0), Next(trigger, Local(2026, 9, 26, 0, 30)));
        Assert.AreEqual(Local(2026, 9, 26, 22, 0), Next(trigger, Local(2026, 9, 26, 2, 0)));
    }

    [TestMethod]
    public void AtFiresOnceAndNeverInThePast()
    {
        var trigger = new Trigger { At = "2026-12-24T18:00" };

        Assert.AreEqual(Local(2026, 12, 24, 18, 0), Next(trigger, Local(2026, 9, 25, 0, 0)));
        Assert.IsNull(Next(trigger, Local(2026, 12, 24, 18, 0)));
        Assert.AreEqual(new DateTimeOffset(2026, 12, 24, 18, 0, 0, TimeSpan.Zero), Next(new Trigger { At = "2026-12-24T18:00Z" }, Local(2026, 9, 25, 0, 0)));
    }

    [TestMethod]
    public void EventTriggersHaveNoFireTime()
    {
        Assert.IsNull(Next(new Trigger { OnStart = true }, Local(2026, 9, 25, 0, 0)));
        Assert.IsNull(Next(new Trigger { FileChanged = "*.png" }, Local(2026, 9, 25, 0, 0)));
    }

    [TestMethod]
    public void DurationsCombineUnits()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(90), TriggerMath.ParseDuration("1h30m"));
        Assert.AreEqual(TimeSpan.FromSeconds(5), TriggerMath.ParseDuration("5s"));
        Assert.IsNull(TriggerMath.ParseDuration("30"));
        Assert.IsNull(TriggerMath.ParseDuration("0m"));
    }

    [TestMethod]
    public void ProblemsNameTheBadField()
    {
        Assert.IsNull(TriggerMath.Problem(new Trigger { Cron = "0 2 * * 1-5" }));
        Assert.IsNotNull(TriggerMath.Problem(new Trigger()));
        Assert.IsNotNull(TriggerMath.Problem(new Trigger { Cron = "0 2 * *", Every = "5m" }));
        StringAssert.Contains(TriggerMath.Problem(new Trigger { Every = "soon" }), "soon");
        StringAssert.Contains(TriggerMath.Problem(new Trigger { Every = "5m", Between = "9-18" }), "9-18");
        StringAssert.Contains(TriggerMath.Problem(new Trigger { At = "tomorrow" }), "tomorrow");
    }
}
