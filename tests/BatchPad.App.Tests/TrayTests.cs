using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class TrayTests
{
    [TestMethod]
    public void ClosingWithEnabledSchedulesHidesToTheTray()
    {
        using var test = new TestWorkspace();
        var (main, tray, _) = Open(test, new Settings());
        Assert.AreEqual(CloseAction.Close, main.ConfirmClose());

        AddEverySchedule(main);

        Assert.IsTrue(tray.IsVisible);
        Assert.AreEqual(CloseAction.HideToTray, main.ConfirmClose());
        Assert.IsTrue(main.IsInTray);
        Assert.AreEqual(CloseAction.Close, main.ConfirmClose(exiting: true));

        var shown = 0;
        main.ShowWindowRequested += () => shown++;
        tray.RaiseOpen();
        Assert.AreEqual(1, shown);
        Assert.IsFalse(main.IsInTray);
    }

    [TestMethod]
    public void SchedulesThatRunInWindowsDoNotKeepBatchPadInTheTray()
    {
        using var test = new TestWorkspace();
        var (main, tray, _) = Open(test, new Settings());

        SchedulesViewModelTests.AddSchedule(main, "hello.bat", editor => editor.RunInWindows = true);

        Assert.IsFalse(tray.IsVisible);
        Assert.AreEqual(CloseAction.Close, main.ConfirmClose());
    }

    [TestMethod]
    public void TheTrayIconShownForANotificationGoesAwayWhenItCloses()
    {
        using var test = new TestWorkspace();
        var (main, tray, _) = Open(test, new Settings());
        main.Tray!.Notify("Done", "", NotificationSeverity.Info, () => { });
        Assert.IsTrue(tray.IsVisible);

        tray.RaiseNotificationClosed();

        Assert.IsFalse(tray.IsVisible);
    }

    [TestMethod]
    public void WithTheSettingOffClosingExits()
    {
        using var test = new TestWorkspace();
        var (main, _, _) = Open(test, new Settings { KeepRunningInTray = false });
        AddEverySchedule(main);

        Assert.AreEqual(CloseAction.Close, main.ConfirmClose());
        Assert.IsFalse(main.IsInTray);
    }

    [TestMethod]
    public async Task AFailedScheduledRunNotifiesOnceAndClickingSelectsItsHistoryEntry()
    {
        using var test = new TestWorkspace();
        var (main, tray, launcher) = Open(test, new Settings());
        AddEverySchedule(main);
        ScheduleFire? fired = null;
        main.Scheduler!.ScheduleFired += fire => fired = fire;

        main.Schedules!.Items.Single().RunNowCommand.Execute(null);
        launcher.Started.Single().Finish(RunOutcome.Exited, 1);
        var record = await fired!.Recorded;

        var (title, _, severity, onClick) = tray.Notifications.Single();
        StringAssert.Contains(title, "hello.bat");
        Assert.AreEqual(NotificationSeverity.Error, severity);
        onClick();
        Assert.AreEqual(record.Id, main.History.Selected?.Record.Id);
        Assert.IsTrue(main.Output.IsHistoryOpen);
    }

    [TestMethod]
    public void TheTrayMenuOpensSchedulesAndExits()
    {
        using var test = new TestWorkspace();
        var (main, tray, _) = Open(test, new Settings());
        main.Schedules!.CloseCommand.Execute(null);
        var exits = 0;
        main.ExitRequested += () => exits++;

        tray.RaiseSchedules();
        tray.RaiseExit();

        Assert.IsNotNull(main.Schedules);
        Assert.AreEqual(1, exits);
    }

    [STATestMethod]
    public void TheRealTrayServiceCanBeCreatedAndDisposed()
    {
        using var tray = new TrayService();
        Assert.IsFalse(tray.IsVisible);
        tray.IsVisible = true;
        Assert.IsTrue(tray.IsVisible);
        tray.IsVisible = false;
    }

    private static (MainViewModel, FakeTray, FakeLauncher) Open(TestWorkspace test, Settings settings)
    {
        var tray = new FakeTray();
        var launcher = new FakeLauncher();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, settings: settings, launcher: launcher, shell: new FakeShell(), confirm: new FakeConfirm(), tray: tray);
        main.OpenSchedulesCommand.Execute(null);
        return (main, tray, launcher);
    }

    private static void AddEverySchedule(MainViewModel main) =>
        SchedulesViewModelTests.AddSchedule(main, "hello.bat", editor =>
        {
            editor.Kind = TriggerKind.Every;
            editor.Every = "1m";
        });
}

