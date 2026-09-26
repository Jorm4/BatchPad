using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workflows;

namespace BatchPad.App.Tests;

/// <summary>Drives the demo's history, lock, workflow, schedule and command-line features with real processes.</summary>
[TestClass]
public sealed class Phase89AcceptanceTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    [TestMethod]
    public async Task TheDemoShowsEachWorkflowAndScheduleFeature()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var main = test.OpenMain(demo, trusted: true);

        var hello = await RunAsync<RunViewModel>(main, "Workspace/Hello/hello.bat");
        Assert.AreEqual("exit 0", hello.StatusText);
        await Eventually(() => main.History.Runs.Any(r => r.Name == "hello.bat" && r.Succeeded));

        var tests = await RunAsync<RunViewModel>(main, "Workspace/Pipeline/Run tests");
        Assert.AreEqual("3 passed · 0 failed · 0 skipped", tests.TestResults!.Summary);

        Select(main, "Workspace/Pipeline/Package");
        main.Details.RunCommand.Execute(null);
        main.Details.RunCommand.Execute(null);
        var (first, second) = ((RunViewModel)main.Output.Tabs[^2], (RunViewModel)main.Output.Tabs[^1]);
        Assert.AreEqual("waiting for lock demo-package", second.StatusText);
        await Task.WhenAll(first.Finished, second.Finished).WaitAsync(Limit);
        Assert.AreEqual("exit 0", second.StatusText);

        var release = await RunAsync<WorkflowRunViewModel>(main, "Workspace/Pipeline/Release pipeline");
        Assert.AreEqual("passed", release.StatusText);
        var run = release.Steps.Select(s => s.Step).ToList();
        CollectionAssert.AreEqual(new[] { "tests", "hello" }, run[1].Members.Select(m => m.Id).ToArray());
        Assert.HasCount(2, run.Single(s => s.Id == "flaky").Attempts);
        Assert.AreEqual("1.4.2", run[0].Outputs["version"]);
        await Eventually(() => release.Steps.Last().Lines.Any(l => l.Text == "Packaging version 1.4.2"));

        Select(main, "Workspace/Pipeline/Release pipeline");
        ((IntFieldViewModel)main.Details.Form!.Field("failures")!).Text = "2";
        var failed = await RunAsync<WorkflowRunViewModel>(main);
        Assert.AreEqual("failed", failed.StatusText);
        Assert.AreEqual("Re-run from Flaky", failed.RerunLabel);
        failed.RerunFromFailedCommand.Execute(null);
        var resumed = (WorkflowRunViewModel)main.Output.Tabs[^1];
        await resumed.Finished.WaitAsync(Limit);
        Assert.AreEqual("passed", resumed.StatusText);
        Assert.AreEqual(StepStatus.Reused, resumed.Steps[0].Status);
        await Eventually(() => main.History.Runs.Any(r => r.Trigger == RunTriggers.ResumedFrom("flaky")));

        main.OpenSchedulesCommand.Execute(null);
        SchedulesViewModelTests.AddSchedule(main, "hello.bat", editor =>
        {
            editor.Kind = TriggerKind.Every;
            editor.Every = "1h";
        });
        ScheduleFire? fired = null;
        main.Scheduler!.ScheduleFired += fire => fired = fire;
        var schedule = main.Schedules!.Items.Single();
        schedule.RunNowCommand.Execute(null);
        await fired!.Recorded.WaitAsync(Limit);
        await Eventually(() => schedule.LastResultText == "exit 0");

        var (code, output) = CliTests.RunShim(test, "run", "release", "--workspace", demo, "--set", "failures=0");
        Assert.AreEqual(0, code, output);
        StringAssert.Contains(output, "Packaging version 1.4.2");
    }

    private static void Select(MainViewModel main, string path) => main.Tree!.Find(path)!.IsSelected = true;

    private static async Task<T> RunAsync<T>(MainViewModel main, string? path = null) where T : OutputTabViewModel
    {
        if (path is not null)
            Select(main, path);
        main.Details.RunCommand.Execute(null);
        var tab = (T)main.Output.Tabs[^1];
        await tab.Finished.WaitAsync(Limit);
        return tab;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsTrue(condition());
    }
}
