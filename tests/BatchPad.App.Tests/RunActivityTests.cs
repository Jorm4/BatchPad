using System.Windows.Shell;
using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using Microsoft.Extensions.Time.Testing;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class RunActivityTests
{
    [TestMethod]
    public async Task TheTaskbarIsIndeterminateDuringARunAndClearAfter()
    {
        using var test = new TestWorkspace();
        var (main, launcher, _, _, _) = Open(test);

        var tab = Run(main);
        Assert.AreEqual(TaskbarItemProgressState.Indeterminate, main.Activity.TaskbarState);

        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        await tab.Finished;
        Assert.AreEqual(TaskbarItemProgressState.None, main.Activity.TaskbarState);
    }

    [TestMethod]
    public async Task AFailureWhileInactiveShowsAnErrorUntilTheWindowIsActivated()
    {
        using var test = new TestWorkspace();
        var (main, launcher, window, _, _) = Open(test);

        var tab = Run(main);
        window.IsActive = false;
        launcher.Started.Single().Finish(RunOutcome.Exited, 1);
        await tab.Finished;
        Assert.AreEqual(TaskbarItemProgressState.Error, main.Activity.TaskbarState);

        window.Activate();
        Assert.AreEqual(TaskbarItemProgressState.None, main.Activity.TaskbarState);
    }

    [TestMethod]
    [DataRow(0, "succeeded, exit 0", NotificationSeverity.Info)]
    [DataRow(1, "failed, exit 1", NotificationSeverity.Error)]
    public async Task ALongRunFinishingWhileInactiveNotifiesOnceAndClickingSelectsItsTab(int exitCode, string outcome, NotificationSeverity severity)
    {
        using var test = new TestWorkspace();
        var (main, launcher, window, tray, time) = Open(test);

        var tab = Run(main);
        Run(main);
        window.IsActive = false;
        time.Advance(TimeSpan.FromSeconds(12));
        launcher.Started[0].Finish(RunOutcome.Exited, exitCode);
        await tab.Finished;

        var (title, text, actual, onClick) = tray.Notifications.Single();
        Assert.AreEqual("hello.bat", title);
        Assert.AreEqual($"{outcome}, {OutputTabViewModel.FormatDuration(TimeSpan.FromSeconds(12))}", text);
        Assert.AreEqual(severity, actual);
        Assert.AreNotSame(tab, main.Output.SelectedTab);
        var shown = 0;
        main.ShowWindowRequested += () => shown++;
        onClick();
        Assert.AreSame(tab, main.Output.SelectedTab);
        Assert.AreEqual(1, shown);
    }

    [TestMethod]
    [DataRow(5, false)]
    [DataRow(12, true)]
    public async Task AShortRunOrAnActiveWindowDoesNotNotify(int seconds, bool active)
    {
        using var test = new TestWorkspace();
        var (main, launcher, window, tray, time) = Open(test);

        var tab = Run(main);
        window.IsActive = active;
        time.Advance(TimeSpan.FromSeconds(seconds));
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        await tab.Finished;

        Assert.IsEmpty(tray.Notifications);
    }

    [TestMethod]
    public async Task AFailedScheduledRunNotifiesOnlyOnce()
    {
        using var test = new TestWorkspace();
        var (main, launcher, window, tray, time) = Open(test);
        main.OpenSchedulesCommand.Execute(null);
        SchedulesViewModelTests.AddSchedule(main, "hello.bat", editor =>
        {
            editor.Kind = TriggerKind.Every;
            editor.Every = "1m";
        });
        ScheduleFire? fired = null;
        main.Scheduler!.ScheduleFired += fire => fired = fire;

        main.Schedules!.Items.Single().RunNowCommand.Execute(null);
        window.IsActive = false;
        time.Advance(TimeSpan.FromSeconds(12));
        launcher.Started.Single().Finish(RunOutcome.Exited, 1);
        await fired!.Recorded;
        await main.Output.Tabs.Single().Finished;

        StringAssert.StartsWith(tray.Notifications.Single().Title, "Scheduled run failed");
    }

    [TestMethod]
    public async Task AWorkflowRunningAloneReportsItsFinishedSteps()
    {
        using var test = new TestWorkspace();
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "ws")).FullName;
        string Gate(int step) => Path.Combine(directory, $"{step}.go");
        try
        {
            foreach (var step in new[] { 1, 2, 3 })
                File.WriteAllText(Path.Combine(directory, $"step{step}.bat"), $"@echo off\r\n:wait\r\nif not exist \"{Gate(step)}\" goto wait\r\n");
            File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
                { "id": "progress", "scripts": [
                  { "id": "one", "name": "One", "path": "step1.bat" },
                  { "id": "two", "name": "Two", "path": "step2.bat" },
                  { "id": "three", "name": "Three", "path": "step3.bat" },
                  { "id": "flow", "name": "Flow", "steps": [ { "run": "one" }, { "run": "two" }, { "run": "three" } ] } ] }
                """);
            var main = test.OpenMain(directory, trusted: true);
            main.Select("Workspace/Flow");
            main.Details.RunCommand.Execute(null);
            var tab = (WorkflowRunViewModel)main.Output.Tabs.Single();

            foreach (var step in new[] { 1, 2 })
            {
                File.WriteAllText(Gate(step), "");
                await Eventually(() => main.Activity is { TaskbarState: TaskbarItemProgressState.Normal } activity
                    && Math.Abs(activity.TaskbarProgress - step / 3.0) < 0.001, () => $"{main.Activity.TaskbarState} {main.Activity.TaskbarProgress}");
            }
            File.WriteAllText(Gate(3), "");
            await tab.Finished.WaitAsync(Limit);
            Assert.AreEqual(TaskbarItemProgressState.None, main.Activity.TaskbarState);
        }
        finally
        {
            foreach (var step in new[] { 1, 2, 3 })
                File.WriteAllText(Gate(step), "");
        }
    }

    private static (MainViewModel, FakeLauncher, FakeWindow, FakeTray, FakeTimeProvider) Open(TestWorkspace test)
    {
        var launcher = new FakeLauncher();
        var tray = new FakeTray();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 27, 9, 0, 0, TimeSpan.Zero));
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: launcher, shell: new FakeShell(), confirm: new FakeConfirm(),
            time: time, tray: tray);
        var window = new FakeWindow();
        main.Activity.Window = window;
        return (main, launcher, window, tray, time);
    }

    private static OutputTabViewModel Run(MainViewModel main)
    {
        main.Select("Workspace/Hello/hello.bat");
        main.Details.RunCommand.Execute(null);
        return main.Output.Tabs.Last();
    }
}
