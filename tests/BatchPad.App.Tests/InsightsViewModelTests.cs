using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Insights;
using BatchPad.Core.History;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class InsightsViewModelTests
{
    [TestMethod]
    public void ThePageShowsTimeSharesScriptFiguresAndTests()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var hello = main.Tree!.Find("Workspace/Hello/hello.bat")!.Key;
        var build = main.Tree.Find("Workspace/Build & run")!.Key;
        var now = main.Time.GetUtcNow();
        Record(main, hello, "hello.bat", now.AddMinutes(-50), 10, "Hello", tests: new TestSummary(4, 0, 0, [], []));
        Record(main, hello, "hello.bat", now.AddMinutes(-40), 20, "Hello", exitCode: 1,
            tests: new TestSummary(3, 1, 0, ["Suite.Flaky"], [new TestTiming("Suite.Slow", 4.5)]));
        Record(main, hello, "hello.bat", now.AddMinutes(-30), 30, "Hello", trigger: "agent:claude-code",
            tests: new TestSummary(4, 0, 0, [], [new TestTiming("Suite.Slow", 3)]));
        Record(main, build, "Build & run", now.AddMinutes(-20), 40, null, trigger: RunTriggers.Schedule("nightly"));
        Record(main, build, "Build & run", now.AddDays(-10), 1000, null);

        main.OpenInsightsCommand.Execute(null);
        var page = main.Insights!;

        Assert.IsTrue(main.IsPageOpen);
        Assert.AreEqual("7d", page.Period);
        Assert.AreEqual("hello.bat", page.Scripts[0].Name);
        Assert.AreEqual("3", page.Scripts[0].RunsText);
        Assert.AreEqual(Duration(20), page.Scripts[0].MedianText);
        Assert.AreEqual(Duration(30), page.Scripts[0].P95Text);
        Assert.AreEqual("33%", page.Scripts[0].FailureRateText);
        Assert.IsTrue(page.Scripts[0].HasFailures);
        Assert.AreEqual("Build & run", page.Scripts[1].Name);
        Assert.AreEqual("1", page.Scripts[1].RunsText);
        CollectionAssert.AreEqual(new[] { "Hello", "(top level)" }, page.Folders.Select(f => f.Name).ToList());
        Assert.AreEqual("40%", page.Folders[1].ShareText);
        var triggers = page.Triggers.ToDictionary(t => t.Name);
        Assert.AreEqual("30%", triggers["You"].ShareText);
        Assert.AreEqual("30%", triggers["Agents"].ShareText);
        Assert.AreEqual("40%", triggers["Schedules"].ShareText);
        Assert.AreEqual("Suite.Slow", page.SlowestTests.Single().Name);
        Assert.AreEqual(Duration(4.5), page.SlowestTests.Single().Detail);
        Assert.AreEqual("Suite.Flaky", page.FlakyTests.Single().Name);
        Assert.AreEqual("hello.bat", page.Repeats.Single().Name);

        page.Period = "30d";

        Assert.AreEqual("Build & run", page.Scripts[0].Name);
        Assert.AreEqual(5, page.Stats!.Runs);
    }

    [TestMethod]
    public void ANewRecordUpdatesThePageLive()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var hello = main.Tree!.Find("Workspace/Hello/hello.bat")!.Key;
        main.OpenInsightsCommand.Execute(null);
        var page = main.Insights!;
        Assert.IsTrue(page.IsEmpty);
        var changed = new List<string?>();
        page.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var now = main.Time.GetUtcNow();
        for (var i = 0; i < 3; i++)
            Record(main, hello, "hello.bat", now.AddMinutes(-3 + i), 1, "Hello");

        Assert.IsFalse(page.IsEmpty);
        Assert.AreEqual("3", page.Scripts.Single().RunsText);
        Assert.AreEqual("3× in 2 min", page.Repeats.Single().CountText);
        Assert.IsNotEmpty(changed);
    }

    [TestMethod]
    public void HeadersSortTheScriptsAndARowSelectsItsScript()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var helloNode = main.Tree!.Find("Workspace/Hello/hello.bat")!;
        var build = main.Tree.Find("Workspace/Build & run")!.Key;
        var now = main.Time.GetUtcNow();
        Record(main, helloNode.Key, "hello.bat", now.AddMinutes(-9), 5, "Hello");
        Record(main, helloNode.Key, "hello.bat", now.AddMinutes(-8), 5, "Hello");
        Record(main, build, "Build & run", now.AddMinutes(-7), 8, null);
        main.OpenInsightsCommand.Execute(null);
        var page = main.Insights!;

        CollectionAssert.AreEqual(new[] { "hello.bat", "Build & run" }, page.Scripts.Select(s => s.Name).ToList());
        page.SortCommand.Execute(ScriptSort.Median);
        CollectionAssert.AreEqual(new[] { "Build & run", "hello.bat" }, page.Scripts.Select(s => s.Name).ToList());
        page.SortCommand.Execute(ScriptSort.Median);
        CollectionAssert.AreEqual(new[] { "hello.bat", "Build & run" }, page.Scripts.Select(s => s.Name).ToList());
        page.SortCommand.Execute(ScriptSort.Name);
        CollectionAssert.AreEqual(new[] { "Build & run", "hello.bat" }, page.Scripts.Select(s => s.Name).ToList());

        page.SelectScriptCommand.Execute(helloNode.Key);

        Assert.AreSame(helloNode, main.SelectedNode);
        Assert.IsNotNull(main.Insights);
    }

    [TestMethod]
    public void ThePaletteOpensInsightsAndEscapeClosesIt()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);

        main.Palette.OpenCommand.Execute(null);
        main.Palette.Query = "insights";
        main.Palette.RunCommand.Execute(null);
        Assert.IsNotNull(main.Insights);

        main.OpenSchedulesCommand.Execute(null);
        Assert.IsNull(main.Insights);
        main.OpenInsightsCommand.Execute(null);
        Assert.IsNull(main.Schedules);

        main.Reload();
        Assert.IsNotNull(main.Insights);
        main.EscapeCommand.Execute(null);
        Assert.IsNull(main.Insights);
        Assert.IsFalse(main.IsPageOpen);
    }

    private static string Duration(double seconds) => OutputTabViewModel.FormatDuration(TimeSpan.FromSeconds(seconds));

    [TestMethod]
    public void ChoosingAScriptClosesInsightsButAReloadDoesNot()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true);
        main.Select("Workspace/Hello/hello.bat");
        main.OpenInsightsCommand.Execute(null);

        main.ReloadFromDiskCommand.Execute(null);
        Assert.IsNotNull(main.Insights);

        main.Select("Workspace/Hello/hello.py");
        Assert.IsNull(main.Insights);
        Assert.IsFalse(main.IsPageOpen);
        Assert.AreEqual("hello.py", main.Details.Node!.Name);
    }

    private static void Record(MainViewModel main, string key, string name, DateTimeOffset startedAt, double seconds, string? folder,
        int exitCode = 0, string trigger = RunTriggers.Manual, TestSummary? tests = null) =>
        main.History.Store!.Add(new RunRecord
        {
            NodeKey = key,
            Name = name,
            StartedAt = startedAt,
            Duration = TimeSpan.FromSeconds(seconds),
            Outcome = RunOutcome.Exited,
            ExitCode = exitCode,
            Folder = folder,
            Trigger = trigger,
            Tests = tests,
        }, []);
}
