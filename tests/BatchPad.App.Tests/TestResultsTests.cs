using BatchPad.App.ViewModels;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class TestResultsTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "junit", name);

    [TestMethod]
    public async Task ReRunFailedRunsAgainWithTheFailedSuitesInTheRerunParameter()
    {
        using var test = new TestWorkspace();
        var report = Path.Combine(test.Root, "junit.xml");
        var (main, launcher) = Open(test, new TestReportDefinition { Path = report, RerunParam = "match", RerunBy = RerunBy.Suite });

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        File.WriteAllText(report, File.ReadAllText(Fixture("gtest.xml")));
        launcher.Started.Single().Finish(RunOutcome.Exited, 1);
        await run.Finished;

        var results = run.TestResults!;
        Assert.AreEqual("3 passed · 2 failed · 1 skipped", results.Summary);
        results.FailuresOnly = true;
        CollectionAssert.AreEqual(new[] { "MathTest", "ParserTest" }, results.Suites.Select(s => s.Name).ToList());
        Assert.AreEqual("Divides", results.Suites[0].Cases.Single().Name);
        StringAssert.StartsWith(results.Suites[0].Cases.Single().Message, "math_test.cpp:12");
        Assert.IsTrue(results.CanRerun);

        results.RerunFailedCommand.Execute(null);

        Assert.HasCount(2, launcher.Requests);
        Assert.AreEqual("MathTest ParserTest", launcher.Requests[1].Values!["match"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task WithoutRerunParamThereIsNoReRunAndAStaleReportIsIgnored()
    {
        using var test = new TestWorkspace();
        var report = Path.Combine(test.Root, "junit.xml");
        File.Copy(Fixture("pytest.xml"), report);
        File.SetLastWriteTimeUtc(report, DateTime.UtcNow.AddMinutes(-5));
        var (main, launcher) = Open(test, new TestReportDefinition { Path = report });

        main.Details.RunCommand.Execute(null);
        var stale = (RunViewModel)main.Output.Tabs.Single();
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        await stale.Finished;
        Assert.IsNull(stale.TestResults);

        main.Details.RunCommand.Execute(null);
        var fresh = (RunViewModel)main.Output.Tabs[1];
        File.SetLastWriteTimeUtc(report, DateTime.UtcNow);
        launcher.Started[1].Finish(RunOutcome.Exited, 1);
        await fresh.Finished;

        Assert.AreEqual(2, fresh.TestResults!.Failed);
        Assert.IsFalse(fresh.TestResults.CanRerun);
    }

    private static (MainViewModel, FakeLauncher) Open(TestWorkspace test, TestReportDefinition testReport)
    {
        var launcher = new FakeLauncher();
        var main = new MainViewModel(test.Paths, new Settings(), launcher, shell: new FakeShell());
        main.Trust.Trust(TestWorkspace.DemoSource);
        main.Open(Path.Combine(TestWorkspace.DemoSource, "batchpad.json"));
        var node = main.Tree!.Find("Workspace/Hello/hello.bat")!;
        node.Script!.TestReport = testReport;
        node.Script.Params = [new ParameterDefinition { Name = "match", Type = ParameterType.Text }];
        node.IsSelected = true;
        return (main, launcher);
    }
}
