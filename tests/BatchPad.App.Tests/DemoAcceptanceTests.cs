using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

/// <summary>Drives the demo's history, lock, workflow, schedule and command-line features with real processes.</summary>
[TestClass]
public sealed class DemoAcceptanceTests
{
    [TestMethod]
    public async Task TheDemoShowsEachWorkflowAndScheduleFeature()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var trust = new TrustStore(new Settings(), Path.Combine(test.Root, "gate.json"));
        trust.Trust(demo);
        var gate = new RunGate(trust);
        var main = test.OpenMain(demo, trusted: true, launcher: new GatedRunLauncher(gate, new InterpreterLocator()));

        var hello = await RunAsync<RunViewModel>(main, "Workspace/Hello/hello.bat");
        Assert.AreEqual("exit 0", hello.StatusText);
        await Eventually(() => main.History.Store!.Recent().Any(r => r.Name == "hello.bat" && r.Succeeded));

        var tests = await RunAsync<RunViewModel>(main, "Workspace/Pipeline/Run tests");
        Assert.AreEqual("3 passed · 0 failed · 0 skipped", tests.TestResults!.Summary);

        var held = await gate.Locks.AcquireAsync([LockKeys.For("demo-package", null, Checkout.DirectoryOf(demo))], new object());
        main.Select("Workspace/Pipeline/Package");
        main.Details.RunCommand.Execute(null);
        var package = (RunViewModel)main.Output.Tabs[^1];
        Assert.AreEqual("waiting for lock demo-package", package.StatusText);
        held.Dispose();
        await package.Finished.WaitAsync(Limit);
        Assert.AreEqual("exit 0", package.StatusText);

        var release = await RunAsync<WorkflowRunViewModel>(main, "Workspace/Pipeline/Release pipeline");
        Assert.AreEqual("passed", release.StatusText);
        var run = release.Steps.Select(s => s.Step).ToList();
        CollectionAssert.AreEqual(new[] { "tests", "hello" }, run[1].Members.Select(m => m.Id).ToArray());
        Assert.HasCount(2, run.Single(s => s.Id == "flaky").Attempts);
        Assert.AreEqual("1.4.2", run[0].Outputs["version"]);
        await Eventually(() => release.Steps.Last().Lines.Any(l => l.Text == "Packaging version 1.4.2"));

        main.Select("Workspace/Pipeline/Release pipeline");
        ((IntFieldViewModel)main.Details.Form!.Field("failures")!).Text = "2";
        var failed = await RunAsync<WorkflowRunViewModel>(main);
        Assert.AreEqual("failed", failed.StatusText);
        failed.RerunFromFailedCommand.Execute(null);
        var resumed = (WorkflowRunViewModel)main.Output.Tabs[^1];
        await resumed.Finished.WaitAsync(Limit);
        Assert.AreEqual("passed", resumed.StatusText);

        main.OpenSchedulesCommand.Execute(null);
        SchedulesViewModelTests.AddSchedule(main, "hello.bat", editor =>
        {
            editor.Kind = TriggerKind.Every;
            editor.Every = "1h";
        });
        var fired = new TaskCompletionSource<ScheduleFire>(TaskCreationOptions.RunContinuationsAsynchronously);
        main.Scheduler!.ScheduleFired += fire => fired.TrySetResult(fire);
        var schedule = main.Schedules!.Items.Single();
        schedule.RunNowCommand.Execute(null);
        await (await fired.Task.WaitAsync(Limit)).Recorded.WaitAsync(Limit);
        await Eventually(() => schedule.LastResultText == "exit 0");

        var (code, output) = CliTests.RunShim(test, "run", "release", "--workspace", demo, "--set", "failures=0");
        Assert.AreEqual(0, code, output);
        StringAssert.Contains(output, "Packaging version 1.4.2");
    }

    private static async Task<T> RunAsync<T>(MainViewModel main, string? path = null) where T : OutputTabViewModel
    {
        if (path is not null)
            main.Select(path);
        main.Details.RunCommand.Execute(null);
        var tab = (T)main.Output.Tabs[^1];
        await tab.Finished.WaitAsync(Limit);
        return tab;
    }
}
