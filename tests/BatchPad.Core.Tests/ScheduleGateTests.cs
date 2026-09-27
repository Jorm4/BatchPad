using BatchPad.Core.Scheduling;
using static BatchPad.Core.Tests.SchedulerTests;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class ScheduleGateTests
{
    [TestMethod]
    public void AConfirmedHashIsAllowedAndMayAnswerConfirm()
    {
        using var h = new Harness("""{ "id": "hourly", "target": "workspace:build", "trigger": { "every": "1h" } }""");
        var entry = ScheduleEntry.For(h.Workspace).Single();
        var hash = DefinitionHash.Of(entry.Target!);
        entry.Schedule.DefinitionHash = hash;

        Assert.AreEqual((Paused: false, Confirmed: true), Outcome(ScheduleGate.Check(entry, hash, h.State, h.Time.GetUtcNow())));
    }

    [TestMethod]
    public void AChangedHashPausesUnlessConfirmedInThisSession()
    {
        using var h = new Harness("""{ "id": "hourly", "target": "workspace:build", "trigger": { "every": "1h" }, "definitionHash": "old" }""");
        var entry = ScheduleEntry.For(h.Workspace).Single();

        Assert.AreEqual((Paused: true, Confirmed: false), Outcome(ScheduleGate.Check(entry, "new", h.State, h.Time.GetUtcNow())));
        Assert.AreEqual((Paused: false, Confirmed: true),
            Outcome(ScheduleGate.Check(entry, "new", h.State, h.Time.GetUtcNow(), confirmedHere: "new")));
    }

    [TestMethod]
    public void AScheduleWithoutAHashAdoptsTheFirstOneItSeesButMayNotAnswerConfirm()
    {
        using var h = new Harness("""{ "id": "hourly", "target": "workspace:build", "trigger": { "every": "1h" } }""");
        var entry = ScheduleEntry.For(h.Workspace).Single();

        Assert.AreEqual((Paused: false, Confirmed: false), Outcome(ScheduleGate.Check(entry, "first", h.State, h.Time.GetUtcNow())));
        Assert.AreEqual("first", h.State.Get(entry.Key)!.AdoptedHash);
        Assert.IsTrue(ScheduleGate.Check(entry, "second", h.State, h.Time.GetUtcNow()).Paused);
    }

    private static (bool Paused, bool Confirmed) Outcome(ScheduleVerdict verdict) => (verdict.Paused, verdict.Confirmed);
}
