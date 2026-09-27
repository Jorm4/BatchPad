using BatchPad.App.ViewModels;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class BenchmarkResultsTests
{
    [TestMethod]
    public async Task AFinishedRunShowsItsBenchmarksAgainstTheDefaultBaseline()
    {
        using var test = new TestWorkspace();
        var (main, launcher, report) = Open(test);
        var older = AddEarlier(main, minutesAgo: 20, parseNs: 800);
        var previous = AddEarlier(main, minutesAgo: 10, parseNs: 1000);

        var run = await Run(main, launcher, report, parseNs: 1300);

        var results = run.BenchmarkResults!;
        Assert.AreEqual(previous.Id, results.Baseline!.Record.Id);
        Assert.AreEqual($"vs {previous.StartedAt.ToLocalTime():d MMM HH:mm}", results.Header);
        CollectionAssert.AreEqual(new[] { previous.Id, older.Id }, results.Baselines.Select(b => b.Record.Id).ToList());
        var row = results.Rows.Single();
        Assert.AreEqual((BenchmarkVerdict.Slower, "+30.0%", "1.30 us", "1.00 us"), (row.Verdict, row.ChangeText, row.TimeText, row.BaselineText));
        Assert.AreEqual("1 benchmark · 1 slower", results.Summary);

        run.ShowBenchmarks = true;
        Assert.IsFalse(run.ShowLines);
        run.ShowTests = true;
        Assert.IsFalse(run.ShowBenchmarks);
    }

    [TestMethod]
    public async Task ChoosingAnotherBaselineRecomputesTheRows()
    {
        using var test = new TestWorkspace();
        var (main, launcher, report) = Open(test);
        var older = AddEarlier(main, minutesAgo: 20, parseNs: 2000);
        AddEarlier(main, minutesAgo: 10, parseNs: 1000);
        var results = (await Run(main, launcher, report, parseNs: 1000)).BenchmarkResults!;
        Assert.AreEqual(BenchmarkVerdict.Unchanged, results.Rows.Single().Verdict);

        results.Baseline = results.Baselines.Single(b => b.Record.Id == older.Id);

        Assert.AreEqual((BenchmarkVerdict.Faster, "-50.0%"), (results.Rows.Single().Verdict, results.Rows.Single().ChangeText));
    }

    [TestMethod]
    public async Task WithoutAnEarlierRunEveryRowIsNew()
    {
        using var test = new TestWorkspace();
        var (main, launcher, report) = Open(test);

        var results = (await Run(main, launcher, report, parseNs: 1000)).BenchmarkResults!;

        Assert.IsNull(results.Baseline);
        Assert.AreEqual("No earlier run to compare with", results.Header);
        Assert.IsTrue(results.Rows.All(r => r is { Verdict: BenchmarkVerdict.New, ChangeText: "new" }));
    }

    [TestMethod]
    public void CompareWithPreviousFromHistoryOpensTheBenchmarks()
    {
        using var test = new TestWorkspace();
        var (main, _, _) = Open(test);
        var previous = AddEarlier(main, minutesAgo: 20, parseNs: 1000);
        AddEarlier(main, minutesAgo: 10, parseNs: 900);
        var entry = main.History.Runs.First();
        Assert.IsTrue(entry.HasBenchmarks);

        entry.CompareWithPreviousCommand.Execute(null);

        var tab = (BenchmarkTabViewModel)main.Output.Tabs.Single();
        Assert.AreSame(tab, main.Output.SelectedTab);
        Assert.AreEqual(previous.Id, tab.Benchmarks.Baseline!.Record.Id);
        Assert.AreEqual(BenchmarkVerdict.Faster, tab.Benchmarks.Rows.Single().Verdict);
        Assert.AreEqual(main.SelectedNode!.Key, tab.Node!.Key);
    }

    private static (MainViewModel, FakeLauncher, string) Open(TestWorkspace test)
    {
        var launcher = new FakeLauncher();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: launcher, shell: new FakeShell());
        var node = main.Tree!.Find("Workspace/Hello/hello.bat")!;
        var report = Path.Combine(test.Root, "bench.json");
        node.Script!.BenchmarkReport = new BenchmarkReportDefinition { Path = report };
        node.IsSelected = true;
        return (main, launcher, report);
    }

    private static RunRecord AddEarlier(MainViewModel main, int minutesAgo, double parseNs) =>
        main.History.Store!.Add(new RunRecord
        {
            NodeKey = main.SelectedNode!.Key,
            Name = main.SelectedNode.Name,
            StartedAt = DateTimeOffset.Now.AddMinutes(-minutesAgo),
            Benchmarks = new BenchmarkSummary([new BenchmarkResult("BM_Parse", parseNs, parseNs, 1000)], BenchmarkReportDefinition.DefaultThreshold),
        }, []);

    private static async Task<RunViewModel> Run(MainViewModel main, FakeLauncher launcher, string report, double parseNs)
    {
        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Last();
        File.WriteAllText(report, $$"""
            { "benchmarks": [ { "name": "BM_Parse", "run_type": "iteration", "iterations": 1000, "real_time": {{parseNs}}, "cpu_time": {{parseNs}}, "time_unit": "ns" } ] }
            """);
        launcher.Started.Last().Finish(RunOutcome.Exited, 0);
        await run.Finished;
        return run;
    }
}
