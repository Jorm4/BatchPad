using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Schedules;
using BatchPad.Core.Config;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class SchedulesViewModelTests
{
    [TestMethod]
    public void AWeekdayScheduleIsSavedToUserJsonWithItsNextRunAndABadge()
    {
        using var test = new TestWorkspace();
        var main = Open(test, TestWorkspace.DemoSource, new FakeLauncher());

        AddSchedule(main, "Build & run", editor => editor.Cron = "0 2 * * 1-5");

        var saved = UserStore.For(main.Workspace!).Load().Schedules!.Single();
        Assert.AreEqual("workspace:build-and-run", saved.Target);
        Assert.AreEqual("0 2 * * 1-5", saved.Trigger.Cron);
        Assert.IsNotNull(saved.DefinitionHash);
        var item = main.Schedules!.Items.Single();
        Assert.AreEqual("Weekdays at 02:00", item.TriggerText);
        var next = item.NextRun!.Value.ToLocalTime();
        Assert.AreEqual(2, next.Hour);
        Assert.IsFalse(next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        var node = main.Tree!.Find("Workspace/Build & run")!;
        Assert.IsTrue(node.IsScheduled);
        Assert.AreEqual("Weekdays at 02:00", node.ScheduleText);
        Assert.IsFalse(main.Tree.Find("Workspace/Hello/hello.bat")!.IsScheduled);
    }

    [TestMethod]
    public async Task RunNowStartsThroughTheLauncherAndShowsTheLastRun()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, TestWorkspace.DemoSource, launcher);
        AddSchedule(main, "hello.bat", editor =>
        {
            editor.Kind = TriggerKind.Every;
            editor.Every = "1m";
        });
        ScheduleFire? fired = null;
        main.Scheduler!.ScheduleFired += fire => fired = fire;
        var item = main.Schedules!.Items.Single();

        item.RunNowCommand.Execute(null);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        await fired!.Recorded;

        Assert.IsTrue(launcher.Requests.Single().Unattended);
        Assert.AreEqual("exit 0", item.LastResultText);
        Assert.IsTrue(item.LastSucceeded);
        var record = main.History.Runs.Single();
        Assert.AreEqual(RunTriggers.Schedule(item.Schedule.Key), record.Trigger);
        Assert.AreEqual("hello.bat", main.Output.Tabs.Single().Title);
    }

    [TestMethod]
    public void DisablingAndEditingRoundTrip()
    {
        using var test = new TestWorkspace();
        var main = Open(test, TestWorkspace.DemoSource, new FakeLauncher());
        AddSchedule(main, "Parameters demo", editor =>
        {
            var game = (ChoiceFieldViewModel)editor.Form!.Field("game")!;
            game.Selected = game.Options.Single(o => o.Value == "Gamma");
            editor.Overlap = OverlapPolicy.Queue;
        });

        main.Schedules!.Items.Single().IsEnabled = false;

        var item = main.Schedules.Items.Single();
        Assert.IsFalse(item.IsEnabled);
        Assert.IsNull(item.NextRun);
        item.EditCommand.Execute(null);
        var editor = main.Schedules.Editor!;
        Assert.AreEqual("Parameters demo", editor.SelectedTarget!.Name);
        Assert.AreEqual(OverlapPolicy.Queue, editor.Overlap);
        Assert.AreEqual("Gamma", editor.Form!.Field("game")!.Value!.GetValue<string>());
        editor.Cron = "30 3 * * *";
        editor.SaveCommand.Execute(null);

        var saved = UserStore.For(main.Workspace!).Load().Schedules!.Single();
        Assert.AreEqual("parameters-demo", saved.Id);
        Assert.AreEqual("30 3 * * *", saved.Trigger.Cron);
        Assert.IsFalse(saved.Enabled);
        Assert.AreEqual(OverlapPolicy.Queue, saved.Overlap);
        Assert.AreEqual("Gamma", saved.Values!["game"]!.GetValue<string>());
        Assert.AreEqual("Daily at 03:30", main.Schedules.Items.Single().TriggerText);
    }

    [TestMethod]
    public void AChangedTargetShowsTheReviewStateUntilReconfirmed()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var launcher = new FakeLauncher();
        var main = Open(test, demo, launcher);
        AddSchedule(main, "hello.bat", _ => { });
        var file = Path.Combine(demo, "batchpad.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"path\": \"hello.bat\"", "\"path\": \"hello.bat\", \"args\": [\"--changed\"]"));

        main.Reload();

        var item = main.Schedules!.Items.Single();
        Assert.IsTrue(item.NeedsReview);
        Assert.AreEqual("Definition changed — review", item.StatusText);
        item.RunNowCommand.Execute(null);
        Assert.IsEmpty(launcher.Requests);

        item.ReconfirmCommand.Execute(null);

        item = main.Schedules.Items.Single();
        Assert.IsFalse(item.NeedsReview);
        Assert.IsNull(item.StatusText);
        Assert.AreEqual(main.Scheduler!.Statuses().Single().DefinitionHash, UserStore.For(main.Workspace!).Load().Schedules!.Single().DefinitionHash);
    }

    [TestMethod]
    public void AnOnStartScheduleFiresOnceWhenTheWorkspaceOpens()
    {
        using var test = new TestWorkspace();
        var first = Open(test, TestWorkspace.DemoSource, new FakeLauncher());
        ConfigWriter.Update(first.Workspace!.MyScripts.FilePath, file =>
            file.Schedules = [new Schedule { Target = "workspace:hello-bat", Trigger = new Trigger { OnStart = true } }]);
        var launcher = new FakeLauncher();

        var main = Open(test, TestWorkspace.DemoSource, launcher);
        main.Reload();

        Assert.AreEqual("hello-bat", launcher.Requests.Single().Script.Id);
    }

    [TestMethod]
    public void ThePaletteOpensSchedules()
    {
        using var test = new TestWorkspace();
        var main = Open(test, TestWorkspace.DemoSource, new FakeLauncher());
        main.EscapeCommand.Execute(null);

        main.Palette.OpenCommand.Execute(null);
        main.Palette.Query = "Schedules";
        main.Palette.RunCommand.Execute(null);

        Assert.IsNotNull(main.Schedules);
    }

    private static MainViewModel Open(TestWorkspace test, string workspace, FakeLauncher launcher)
    {
        var main = new MainViewModel(test.Paths, new Settings(), launcher, shell: new FakeShell(), confirm: new FakeConfirm());
        main.Trust.Trust(workspace);
        main.Open(Path.Combine(workspace, "batchpad.json"));
        main.OpenSchedulesCommand.Execute(null);
        return main;
    }

    internal static void AddSchedule(MainViewModel main, string target, Action<ScheduleEditorViewModel> configure)
    {
        main.Schedules!.AddCommand.Execute(null);
        var editor = main.Schedules.Editor!;
        editor.SelectTarget(target);
        configure(editor);
        editor.SaveCommand.Execute(null);
        Assert.IsNull(editor.Error);
        Assert.IsNull(main.Schedules.Editor);
    }
}
