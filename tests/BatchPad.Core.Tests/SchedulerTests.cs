using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;
using Microsoft.Extensions.Time.Testing;
using static BatchPad.Core.Tests.CronTests;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class SchedulerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private const string Scripts = """
        { "id": "build", "path": "build.bat", "args": ["--fast"] },
        { "id": "flow", "name": "Nightly flow", "steps": [ { "run": "build" } ] }
        """;

    [TestMethod]
    public async Task ACronScheduleFiresAtItsMinuteOnceAndIsRecordedAsScheduled()
    {
        using var h = new Harness("""
            { "id": "nightly", "target": "workspace:build", "values": { "app": "RallyRacer" }, "trigger": { "cron": "0 2 * * *" } }
            """);
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        h.Time.Advance(TimeSpan.FromMinutes(59));
        Assert.IsEmpty(h.Launcher.Runs);
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.HasCount(1, h.Launcher.Runs);
        for (var i = 0; i < 90; i++)
            h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.HasCount(1, h.Launcher.Runs);

        var (request, run) = h.Launcher.Runs.Single();
        var script = Assert.IsInstanceOfType<RunRequest>(request);
        Assert.IsTrue(script.Unattended);
        Assert.AreEqual("RallyRacer", script.Values!["app"]!.GetValue<string>());
        run.Complete(0);
        var record = await h.Fires.Single().Recorded.WaitAsync(Limit);
        Assert.AreEqual(RunTriggers.Schedule("nightly"), record.Trigger);
        Assert.AreEqual("Workspace:id:build", record.NodeKey);
        Assert.AreEqual(Local(2026, 9, 25, 2, 0), record.StartedAt);
        Assert.IsEmpty(h.Failures);
    }

    [TestMethod]
    public void AWorkflowTargetStartsAnUnattendedWorkflow()
    {
        using var h = new Harness("""{ "id": "w", "target": "workspace:flow", "trigger": { "every": "1h" } }""");
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        h.Time.Advance(TimeSpan.FromHours(1));

        var workflow = Assert.IsInstanceOfType<WorkflowRequest>(h.Launcher.Runs.Single().Request);
        Assert.IsTrue(workflow.Unattended);
        Assert.AreEqual("flow", workflow.Workflow.Id);
    }

    [TestMethod]
    public void RunOnceCatchesUpAMissedFireAtStartAndSkipDoesNot()
    {
        using var h = new Harness("""
            { "id": "catch-up", "target": "workspace:build", "trigger": { "cron": "0 2 * * *" }, "missed": "runOnce" },
            { "id": "skipper", "target": "workspace:flow", "trigger": { "cron": "0 2 * * *" } }
            """);
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));
        h.Scheduler.Dispose();

        h.Time.Advance(TimeSpan.FromHours(4));
        using var restarted = h.NewScheduler();
        restarted.Start(ScheduleEntry.For(h.Workspace));

        Assert.IsInstanceOfType<RunRequest>(h.Launcher.Runs.Single().Request);
        restarted.Dispose();
        using var again = h.NewScheduler();
        again.Start(ScheduleEntry.For(h.Workspace));
        Assert.HasCount(1, h.Launcher.Runs);
    }

    [TestMethod]
    public void OverlapSkipDropsAFireWhileRunningAndQueueDefersIt()
    {
        using var h = new Harness("""
            { "id": "skip", "target": "workspace:build", "trigger": { "every": "1h" } },
            { "id": "queue", "target": "workspace:flow", "trigger": { "every": "1h" }, "overlap": "queue" }
            """);
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        h.Time.Advance(TimeSpan.FromHours(1));
        Assert.HasCount(2, h.Launcher.Runs);
        h.Time.Advance(TimeSpan.FromHours(1));
        Assert.HasCount(2, h.Launcher.Runs);

        h.Launcher.Runs.Single(r => r.Request is RunRequest).Run.Complete(0);
        Assert.HasCount(2, h.Launcher.Runs);
        h.Launcher.Runs.Single(r => r.Request is WorkflowRequest).Run.Complete(0);
        Assert.IsTrue(SpinWait.SpinUntil(() => h.Launcher.Runs.Count == 3, Limit));
        Assert.IsInstanceOfType<WorkflowRequest>(h.Launcher.Runs[2].Request);
    }

    [TestMethod]
    public void AChangedDefinitionPausesTheScheduleUntilConfirmed()
    {
        using var h = new Harness("""{ "id": "hourly", "target": "workspace:build", "trigger": { "every": "1h" } }""");
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));
        var paused = new List<SchedulePause>();
        h.Scheduler.SchedulePaused += paused.Add;

        h.WriteWorkspace(Scripts.Replace("--fast", "--everything"));
        h.Scheduler.Update(ScheduleEntry.For(h.Load()));
        h.Time.Advance(TimeSpan.FromHours(3));

        Assert.AreEqual("hourly", paused.Single().Entry.Schedule.Id);
        Assert.IsEmpty(h.Launcher.Runs);
        Assert.IsTrue(h.Scheduler.Statuses().Single().Paused);

        var hash = h.Scheduler.Confirm(h.Scheduler.Statuses().Single().Entry.Key);
        Assert.AreEqual(paused.Single().DefinitionHash, hash);
        h.Time.Advance(TimeSpan.FromHours(1));
        Assert.IsTrue(Assert.IsInstanceOfType<RunRequest>(h.Launcher.Runs.Single().Request).Confirmed);
    }

    [TestMethod]
    public void ADisabledScheduleNeverFires()
    {
        using var h = new Harness("""{ "target": "workspace:build", "trigger": { "every": "30m" }, "enabled": false, "missed": "runOnce" }""");
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        h.Time.Advance(TimeSpan.FromDays(2));
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        Assert.IsEmpty(h.Launcher.Runs);
    }

    [TestMethod]
    public void AFailedRunEmitsScheduleFailed()
    {
        using var h = new Harness("""{ "id": "fails", "target": "workspace:build", "trigger": { "every": "1h" } }""");
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));
        h.Time.Advance(TimeSpan.FromHours(1));

        h.Launcher.Runs.Single().Run.Complete(2);

        Assert.IsTrue(SpinWait.SpinUntil(() => h.Failures.Count == 1, Limit));
        Assert.AreEqual(2, h.Failures[0].Record!.ExitCode);
        StringAssert.Contains(h.Failures[0].Message, "exit code 2");
    }

    [TestMethod]
    public async Task AnUnconfirmedScheduleCannotAnswerConfirmAndAConfirmedOneRunsUnattended()
    {
        using var workspace = new RunWorkspace("""{ "id": "deploy", "path": "exit3.bat", "confirm": "Deploy now?" }""", "exit3.bat");
        var time = new FakeTimeProvider(Local(2026, 9, 25, 1, 0).ToUniversalTime());
        var history = new HistoryStore(workspace.Temp.Path("history"), time);
        using var scheduler = new Scheduler(history, new GatedScheduleLauncher(workspace.Workspace, workspace.Gate, RunWorkspace.Interpreters),
            new ScheduleStateStore(workspace.Temp.Path("schedules.json")), time, Berlin);
        var failures = new List<ScheduleFailure>();
        scheduler.ScheduleFailed += f =>
        {
            lock (failures)
                failures.Add(f);
        };
        var schedule = new Schedule { Id = "deploy", Target = "workspace:deploy", Trigger = new Trigger { Every = "1h" } };
        var entry = ScheduleEntry.Create("deploy", schedule, workspace.Workspace.Workspace, workspace.Workspace);

        scheduler.Start([entry]);
        Assert.IsFalse(scheduler.RunNow("deploy"));
        StringAssert.Contains(failures.Single().Message, "needs confirmation");
        Assert.AreEqual(RunOutcome.FailedToStart, failures.Single().Record!.Outcome);

        schedule.DefinitionHash = DefinitionHash.Of(entry.Target!);
        scheduler.Update([entry]);
        ScheduleFire? fire = null;
        scheduler.ScheduleFired += f => fire = f;
        Assert.IsTrue(scheduler.RunNow("deploy"));
        var record = await fire!.Recorded.WaitAsync(Limit);

        Assert.AreEqual(3, record.ExitCode);
        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            lock (failures)
                return failures.Count == 2;
        }, Limit));
    }

    internal sealed class Harness : IDisposable
    {
        public Harness(string schedules)
        {
            Time.SetLocalTimeZone(Berlin);
            Paths = new AppPaths(Temp.Path("data"), isPortable: false);
            WriteWorkspace(Scripts);
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.UserFile("sched"))!);
            File.WriteAllText(Paths.UserFile("sched"), $$"""{ "schedules": [ {{schedules}} ] }""");
            Workspace = Load();
            History = new HistoryStore(Temp.Path("history"), Time);
            State = new ScheduleStateStore(Temp.Path("local", "schedules.json"));
            Scheduler = NewScheduler();
        }

        public TempDir Temp { get; } = new();
        public FakeTimeProvider Time { get; } = new(Local(2026, 9, 25, 1, 0).ToUniversalTime());
        public AppPaths Paths { get; }
        public LoadedWorkspace Workspace { get; }
        public HistoryStore History { get; }
        public ScheduleStateStore State { get; }
        public RecordingLauncher Launcher { get; } = new();
        public Scheduler Scheduler { get; }
        public List<ScheduleFire> Fires { get; } = [];
        public List<ScheduleFailure> Failures { get; } = [];

        public Scheduler NewScheduler()
        {
            var scheduler = new Scheduler(History, Launcher, State, Time);
            scheduler.ScheduleFired += Fires.Add;
            scheduler.ScheduleFailed += failure =>
            {
                lock (Failures)
                    Failures.Add(failure);
            };
            return scheduler;
        }

        public void WriteWorkspace(string scripts) =>
            File.WriteAllText(Temp.Path("batchpad.json"), $$"""{ "id": "sched", "scripts": [ {{scripts}} ] }""");

        public LoadedWorkspace Load()
        {
            var loaded = WorkspaceLoader.Load(Temp.Path("batchpad.json"), Paths);
            Assert.IsEmpty(loaded.Errors);
            return loaded;
        }

        public void Dispose()
        {
            Scheduler.Dispose();
            Temp.Dispose();
        }
    }

    internal sealed class RecordingLauncher : IScheduleLauncher
    {
        private readonly List<(object Request, FakeRun Run)> _runs = [];

        public List<(object Request, FakeRun Run)> Runs
        {
            get
            {
                lock (_runs)
                    return [.. _runs];
            }
        }

        public IRunOutput Start(RunRequest request) => Add(request);

        public IRunOutput Start(WorkflowRequest request) => Add(request);

        private FakeRun Add(object request)
        {
            var run = new FakeRun();
            lock (_runs)
                _runs.Add((request, run));
            return run;
        }
    }

    internal sealed class FakeRun : IRunOutput
    {
        private readonly TaskCompletionSource<RunResult> _completion = new();

        public Task<RunResult> Completion => _completion.Task;

        public IDisposable Subscribe(Action<OutputLine> onLine) => new CancellationTokenSource();

        public void Complete(int exitCode) => _completion.SetResult(new RunResult(RunOutcome.Exited, exitCode, TimeSpan.FromSeconds(1)));
    }
}
