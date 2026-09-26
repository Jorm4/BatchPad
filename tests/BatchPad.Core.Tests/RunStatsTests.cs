using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class RunStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 18, 0, 0, TimeSpan.Zero);

    internal static RunRecord Run(string key, double minutesAgo, double seconds, string? folder = null, string trigger = RunTriggers.Manual,
        int exitCode = 0, string? id = null, string? parent = null, List<string>? tags = null, TestSummary? tests = null,
        Dictionary<string, JsonNode?>? values = null) => new()
        {
            Id = id ?? Guid.NewGuid().ToString("N"),
            NodeKey = key,
            Name = key,
            StartedAt = Now.AddMinutes(-minutesAgo),
            Duration = TimeSpan.FromSeconds(seconds),
            Outcome = RunOutcome.Exited,
            ExitCode = exitCode,
            Folder = folder,
            Trigger = trigger,
            ParentRunId = parent,
            Tags = tags,
            Tests = tests,
            Values = values ?? [],
        };

    private static TestSummary Tests(int passed, params string[] failed) => new(passed, failed.Length, 0, [.. failed], []);

    [TestMethod]
    public void ScriptsGetMedianP95TrendAndFailureRate()
    {
        double[] seconds = [10, 20, 30, 40, 50, 60, 70, 80, 90, 100];
        var records = seconds.Select((s, i) => Run("build", 600 - i * 60, s, exitCode: i == 3 ? 1 : 0)).ToList();

        var build = RunStats.Compute(records, TimeSpan.FromDays(1), Now).Scripts.Single();

        Assert.AreEqual(10, build.Runs);
        Assert.AreEqual(55, build.MedianSeconds);
        Assert.AreEqual(100, build.P95Seconds);
        Assert.AreEqual(550, build.TotalSeconds);
        Assert.AreEqual(0.1, build.FailureRate, 1e-9);
        Assert.AreEqual(80.0 / 30 - 1, build.Trend!.Value, 1e-9);
        Assert.AreEqual(Now.AddMinutes(-60), build.LastRun);
    }

    [TestMethod]
    public void TimeIsSplitByCheckout()
    {
        Checkout In(string name) => new(name, name == Checkout.MainName ? CheckoutKind.Main : CheckoutKind.Worktree, name, null, null, "repo");
        var records = new[]
        {
            Run("build", 30, 30) with { Checkout = In(Checkout.MainName) },
            Run("build", 20, 60) with { Checkout = In("wt") },
            Run("test", 10, 10) with { Checkout = In("wt") },
        };

        var checkouts = RunStats.Compute(records, TimeSpan.FromDays(1), Now).Checkouts;

        Assert.AreEqual("wt", checkouts[0].Name);
        Assert.AreEqual(70, checkouts[0].Seconds);
        Assert.AreEqual(0.7, checkouts[0].Share, 1e-9);
        Assert.AreEqual(Checkout.MainName, checkouts[1].Name);
        Assert.AreEqual(1, checkouts[1].Runs);
    }

    [TestMethod]
    public void MedianAndPercentileUseTheNearestRank()
    {
        double[] twenty = [.. Enumerable.Range(1, 20).Select(i => (double)i)];

        Assert.AreEqual(19, RunStats.Percentile(twenty, 0.95));
        Assert.AreEqual(10.5, RunStats.Median(twenty));
        Assert.AreEqual(3, RunStats.Median([1, 3, 7]));
        Assert.IsNull(RunStats.Compute([Run("a", 5, 1), Run("a", 4, 2), Run("a", 3, 3)], TimeSpan.FromDays(1), Now).Scripts.Single().Trend);
    }

    [TestMethod]
    public void FolderAndTagSharesCountAWorkflowOnceAndSkipOlderRuns()
    {
        List<RunRecord> records =
        [
            Run("flow", 30, 60, folder: "Test", id: "wf", tags: ["ci"]),
            Run("unit", 30, 40, folder: "Test", parent: "wf"),
            Run("lint", 29, 20, folder: "Lint", parent: "wf"),
            Run("build", 20, 30, folder: "Build", tags: ["ci", "native"]),
            Run("hello", 10, 10),
            Run("old", 60 * 24 * 3, 1000, folder: "Build"),
        ];

        var stats = RunStats.Compute(records, TimeSpan.FromDays(1), Now);

        Assert.AreEqual(5, stats.Runs);
        Assert.AreEqual(100, stats.TotalSeconds);
        CollectionAssert.AreEqual(new[] { "Test", "Build", "" }, stats.Folders.Select(f => f.Name).ToList());
        Assert.AreEqual(0.6, stats.Folders[0].Share, 1e-9);
        Assert.AreEqual(0.3, stats.Folders[1].Share, 1e-9);
        var ci = stats.Tags.Single(t => t.Name == "ci");
        Assert.AreEqual(2, ci.Runs);
        Assert.AreEqual(0.9, ci.Share, 1e-9);
        Assert.AreEqual(0.3, stats.Tags.Single(t => t.Name == "native").Share, 1e-9);
        Assert.AreEqual(40, stats.Scripts.Single(s => s.NodeKey == "unit").TotalSeconds);
    }

    [TestMethod]
    public void AStepWhoseWorkflowIsOutsideThePeriodCountsOnItsOwn()
    {
        var stats = RunStats.Compute([Run("unit", 30, 40, folder: "Test", parent: "gone")], TimeSpan.FromDays(1), Now);

        Assert.AreEqual(40, stats.TotalSeconds);
        Assert.AreEqual(1.0, stats.Folders.Single().Share);
    }

    [TestMethod]
    public void TimeIsSplitByTriggerClass()
    {
        List<RunRecord> records =
        [
            Run("a", 50, 40, trigger: RunTriggers.Manual),
            Run("a", 49, 10, trigger: RunTriggers.ResumedFrom("two")),
            Run("b", 40, 30, trigger: "agent:claude-code"),
            Run("b", 39, 10, trigger: "agent:other"),
            Run("c", 30, 5, trigger: RunTriggers.Schedule("nightly")),
            Run("c", 29, 3, trigger: RunTriggers.AfterRunOf("after")),
            Run("d", 20, 2, trigger: RunTriggers.Cli),
        ];

        var triggers = RunStats.Compute(records, TimeSpan.FromDays(1), Now).Triggers.ToDictionary(t => t.Name);

        Assert.AreEqual(50, triggers[TriggerClasses.You].Seconds);
        Assert.AreEqual(40, triggers[TriggerClasses.Agents].Seconds);
        Assert.AreEqual(2, triggers[TriggerClasses.Agents].Runs);
        Assert.AreEqual(8, triggers[TriggerClasses.Schedules].Seconds);
        Assert.AreEqual(0.02, triggers[TriggerClasses.Cli].Share, 1e-9);
    }

    [TestMethod]
    public void ThreeRunsWithTheSameValuesWithinHalfAnHourAreARepeat()
    {
        Dictionary<string, JsonNode?> Release() => new() { ["config"] = "release" };
        List<RunRecord> records =
        [
            Run("build", 100, 1, values: Release()),
            Run("build", 90, 1, values: Release()),
            Run("build", 80, 1, values: Release()),
            Run("build", 75, 1, values: Release()),
            Run("build", 10, 1, values: Release()),
            Run("build", 85, 1, values: new() { ["config"] = "debug" }),
            Run("test", 50, 1),
            Run("test", 40, 1),
            Run("test", 5, 1),
            Run("step", 3, 1, parent: "wf"),
            Run("flow", 3, 3, id: "wf"),
            Run("step", 2, 1, parent: "wf2"),
            Run("flow", 2, 3, id: "wf2"),
            Run("step", 1, 1, parent: "wf3"),
            Run("flow", 1, 3, id: "wf3"),
        ];

        var repeats = RunStats.Compute(records, TimeSpan.FromDays(1), Now).Repeats;

        Assert.HasCount(2, repeats);
        var build = repeats[0];
        Assert.AreEqual("build", build.NodeKey);
        Assert.AreEqual(4, build.Count);
        Assert.AreEqual("release", build.Values["config"]!.GetValue<string>());
        Assert.AreEqual(Now.AddMinutes(-100), build.First);
        Assert.AreEqual(Now.AddMinutes(-75), build.Last);
        Assert.AreEqual("flow", repeats[1].NodeKey);
    }

    [TestMethod]
    public void SlowestTestsKeepEachTestsLongestTime()
    {
        List<RunRecord> records =
        [
            Run("tests", 30, 60, tests: new(2, 0, 0, [], [new("Suite.Slow", 9), new("Suite.Quick", 1)])),
            Run("tests", 20, 60, tests: new(2, 0, 0, [], [new("Suite.Slow", 12), new("Suite.Quick", 2)])),
            Run("other", 10, 60, tests: new(1, 0, 0, [], [new("Other.Medium", 5)])),
        ];

        var slowest = RunStats.Compute(records, TimeSpan.FromDays(1), Now).SlowestTests;

        CollectionAssert.AreEqual(new[] { "Suite.Slow", "Other.Medium", "Suite.Quick" }, slowest.Select(t => t.Name).ToList());
        Assert.AreEqual(12, slowest[0].Seconds);
        Assert.AreEqual("tests", slowest[0].NodeKey);
    }

    [TestMethod]
    public void StoppedRunsCountAsTimeSpentButNotInTheFailureRateOrTimings()
    {
        var records = new[] { 10.0, 20, 30, 40 }.Select((s, i) => Run("serve", 60 - i * 10, s, exitCode: i == 0 ? 1 : 0)).ToList();
        records.Add(Run("serve", 5, 1000) with { Outcome = RunOutcome.Stopped, ExitCode = 1 });

        var serve = RunStats.Compute(records, TimeSpan.FromDays(1), Now).Scripts.Single();

        Assert.AreEqual(5, serve.Runs);
        Assert.AreEqual(1, serve.Failures);
        Assert.AreEqual(0.25, serve.FailureRate, 1e-9);
        Assert.AreEqual(1100, serve.TotalSeconds);
        Assert.AreEqual(25, serve.MedianSeconds);
        Assert.AreEqual(40, serve.P95Seconds);
        Assert.AreEqual(35.0 / 15 - 1, serve.Trend!.Value, 1e-9);
        Assert.AreEqual(Now.AddMinutes(-5), serve.LastRun);
    }

    [TestMethod]
    public void ATestThatFailsAgainAfterPassingIsFlakyButOneThatWasFixedIsNot()
    {
        List<RunRecord> records =
        [
            Run("tests", 50, 1, exitCode: 1, tests: Tests(8, "A.Flaky", "A.Broken", "A.Fixed")),
            Run("tests", 40, 1, exitCode: 1, tests: Tests(9, "A.Broken", "A.Fixed")),
            Run("tests", 30, 1, exitCode: 1, tests: Tests(8, "A.Flaky", "A.Broken")),
            Run("tests", 20, 1, exitCode: 1, tests: new(5, 60, 0, ["A.Broken"], [])),
            Run("other", 30, 1, tests: Tests(3, "B.NeverPassed")),
        ];

        var flaky = RunStats.Compute(records, TimeSpan.FromDays(1), Now).FlakyTests.Single();

        Assert.AreEqual("A.Flaky", flaky.Name);
        Assert.AreEqual("tests", flaky.NodeKey);
        Assert.AreEqual(2, flaky.Failures);
        Assert.AreEqual(1, flaky.Passes);
    }

    [TestMethod]
    public void ATestThatPassedFailedAndPassedAgainIsFlaky()
    {
        List<RunRecord> records =
        [
            Run("tests", 30, 1, tests: Tests(10)),
            Run("tests", 20, 1, exitCode: 1, tests: Tests(9, "A.Regressed")),
            Run("tests", 10, 1, tests: Tests(10)),
        ];

        var flaky = RunStats.Compute(records, TimeSpan.FromDays(1), Now).FlakyTests.Single();

        Assert.AreEqual("A.Regressed", flaky.Name);
        Assert.AreEqual(1, flaky.Failures);
        Assert.AreEqual(1, flaky.Passes);
    }

    [TestMethod]
    public void ThePeriodParsesDaysAndHours()
    {
        Assert.IsTrue(RunStats.TryParsePeriod("7d", out var week));
        Assert.AreEqual(TimeSpan.FromDays(7), week);
        Assert.IsTrue(RunStats.TryParsePeriod("12h", out var hours));
        Assert.AreEqual(TimeSpan.FromHours(12), hours);
        Assert.IsFalse(RunStats.TryParsePeriod("7x", out _));
        Assert.IsFalse(RunStats.TryParsePeriod("0d", out _));
        Assert.IsFalse(RunStats.TryParsePeriod("", out _));
    }

    [TestMethod]
    public void TheStatsRoundTripAsJson()
    {
        var stats = RunStats.Compute([Run("build", 5, 1, values: new() { ["n"] = 1 }), Run("build", 4, 1, values: new() { ["n"] = 1 }),
            Run("build", 3, 1, values: new() { ["n"] = 1 })], TimeSpan.FromDays(1), Now);

        var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<RunStats>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        StringAssert.Contains(json, "\"medianSeconds\":1");
        Assert.AreEqual(3, back.Scripts.Single().Runs);
        Assert.AreEqual(3, back.Repeats.Single().Count);
    }
}
