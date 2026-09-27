using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Output;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class BenchmarkComparisonTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 26, 14, 2, 0, TimeSpan.Zero);

    [TestMethod]
    public void EachBenchmarkGetsAVerdictAndTheSlowestComeFirst()
    {
        var changes = BenchmarkComparison.Compare(
            [Result("slower", 100), Result("faster", 100), Result("same", 100), Result("gone", 100)],
            [Result("same", 102), Result("faster", 80), Result("slower", 120), Result("new", 50)], threshold: 5);

        CollectionAssert.AreEqual(new[] { "slower", "same", "faster", "new", "gone" }, changes.Select(c => c.Name).ToList());
        CollectionAssert.AreEqual(
            new[] { BenchmarkVerdict.Slower, BenchmarkVerdict.Unchanged, BenchmarkVerdict.Faster, BenchmarkVerdict.New, BenchmarkVerdict.Gone },
            changes.Select(c => c.Verdict).ToList());
        Assert.AreEqual(20, changes[0].ChangePercent!.Value, 1e-9);
        Assert.AreEqual((100.0, 120.0), (changes[0].BaselineNs, changes[0].CurrentNs));
        Assert.IsNull(changes[3].BaselineNs);
        Assert.IsNull(changes[4].CurrentNs);
    }

    [TestMethod]
    public void ExactlyTheThresholdIsUnchanged()
    {
        var changes = BenchmarkComparison.Compare([Result("up", 100), Result("down", 100)], [Result("up", 105), Result("down", 95)], threshold: 5);

        Assert.IsTrue(changes.All(c => c.Verdict == BenchmarkVerdict.Unchanged));
    }

    [TestMethod]
    public void ABaselineTimeOfZeroHasNoChangeAndIsUnchanged()
    {
        var change = BenchmarkComparison.Compare([Result("noop", 0)], [Result("noop", 5)], threshold: 5).Single();

        Assert.AreEqual((0.0, 5.0, (double?)null, BenchmarkVerdict.Unchanged), (change.BaselineNs, change.CurrentNs, change.ChangePercent, change.Verdict));
    }

    [TestMethod]
    public void CpuTimeIsComparedOnRequest()
    {
        var baseline = new[] { new BenchmarkResult("io", 100, 10, 1) };
        var current = new[] { new BenchmarkResult("io", 100, 20, 1) };

        Assert.AreEqual(BenchmarkVerdict.Unchanged, BenchmarkComparison.Compare(baseline, current, 5).Single().Verdict);
        Assert.AreEqual(BenchmarkVerdict.Slower, BenchmarkComparison.Compare(baseline, current, 5, cpu: true).Single().Verdict);
    }

    [TestMethod]
    public void TheDefaultBaselineIsTheLatestEarlierSuccessfulRunWithTheSameValues()
    {
        var current = Record("current", 10, "smoke");
        var match = Record("match", 5, "smoke");
        var history = new[]
        {
            current,
            Record("deep", 9, "deep"),
            Record("failed", 8, "smoke") with { ExitCode = 1 },
            Record("none", 7, "smoke") with { Benchmarks = null },
            Record("other", 6, "smoke") with { NodeKey = "Workspace:id:other" },
            match,
            Record("older", 4, "smoke"),
            Record("later", 11, "smoke"),
        };

        Assert.AreEqual("match", BenchmarkComparison.DefaultBaseline(current, history)?.Id);
        Assert.IsNull(BenchmarkComparison.DefaultBaseline(match with { Values = new() { ["depth"] = "x" } }, history));
    }

    [TestMethod]
    public void WithoutABaselineEveryBenchmarkIsNew()
    {
        var comparison = BenchmarkComparison.Of(Record("current", 10, "smoke"), null);

        Assert.IsNull(comparison.BaselineRunId);
        Assert.IsTrue(comparison.Benchmarks.All(b => b.Verdict == BenchmarkVerdict.New));
        Assert.IsFalse(comparison.Regressed);
    }

    private static BenchmarkResult Result(string name, double ns) => new(name, ns, ns, 1000);

    private static RunRecord Record(string id, int minutes, string depth) => new()
    {
        Id = id,
        NodeKey = "Workspace:id:bench",
        Name = "Bench",
        StartedAt = Start.AddMinutes(minutes),
        Values = new Dictionary<string, JsonNode?> { ["depth"] = depth },
        Benchmarks = new BenchmarkSummary([Result("parse", 100)], 5),
    };
}
