using System.Collections.Concurrent;
using BatchPad.Core.Discovery;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workflows;
using Harness = BatchPad.Core.Tests.SchedulerTests.Harness;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class EventTriggerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [TestMethod]
    public void ThreeMatchingFilesWithinTheDebounceFireOnceAndANonMatchingFileNever()
    {
        using var h = new Harness("""{ "id": "regen", "target": "workspace:build", "trigger": { "fileChanged": "assets/*.png", "debounce": "5s" } }""");
        Directory.CreateDirectory(h.Temp.Path("assets"));
        using var watcher = new FolderWatcher([h.Temp.Root], TimeSpan.FromSeconds(1));
        using var triggers = EventTriggers.Register(h.Scheduler, h.Workspace, watcher);
        var seen = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        watcher.FileChanged += (_, change) => seen[Path.GetFileName(change.FullPath)] = true;
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        File.WriteAllText(h.Temp.Path("assets", "notes.txt"), "x");
        Assert.IsTrue(SpinWait.SpinUntil(() => seen.ContainsKey("notes.txt"), Limit));
        h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.IsEmpty(h.Launcher.Runs);

        string[] images = ["a.png", "b.png", "c.png"];
        foreach (var image in images)
            File.WriteAllText(h.Temp.Path("assets", image), "x");
        Assert.IsTrue(SpinWait.SpinUntil(() => images.All(seen.ContainsKey), Limit));
        h.Time.Advance(TimeSpan.FromSeconds(4));
        Assert.IsEmpty(h.Launcher.Runs);
        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.HasCount(1, h.Launcher.Runs);
    }

    [TestMethod]
    public void OnStartFiresOnceWhenTheSchedulerStarts()
    {
        using var h = new Harness("""{ "id": "serve", "target": "workspace:build", "trigger": { "onStart": true } }""");
        using var triggers = EventTriggers.Register(h.Scheduler, h.Workspace, null);
        Assert.IsEmpty(h.Launcher.Runs);

        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));
        h.Scheduler.Update(ScheduleEntry.For(h.Workspace));
        h.Time.Advance(TimeSpan.FromDays(1));

        Assert.IsInstanceOfType<RunRequest>(h.Launcher.Runs.Single().Request);
    }

    [TestMethod]
    public void AfterRunOnFailureFiresAfterAFailedRunAndNotAfterASuccess()
    {
        using var h = new Harness("""{ "id": "alert", "target": "workspace:flow", "trigger": { "afterRun": "workspace:build", "result": "failure" } }""");
        using var triggers = EventTriggers.Register(h.Scheduler, h.Workspace, null);
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        RecordBuild(h, exitCode: 0);
        Assert.IsEmpty(h.Launcher.Runs);
        RecordBuild(h, exitCode: 2);
        Assert.IsInstanceOfType<WorkflowRequest>(h.Launcher.Runs.Single().Request);
    }

    [TestMethod]
    public void AnAfterRunLoopStopsAtTheGuardWithAReason()
    {
        using var h = new Harness("""
            { "id": "then-flow", "target": "workspace:flow", "trigger": { "afterRun": "workspace:build", "result": "always" } },
            { "id": "then-build", "target": "workspace:build", "trigger": { "afterRun": "workspace:flow" } }
            """);
        using var triggers = EventTriggers.Register(h.Scheduler, h.Workspace, null);
        h.Scheduler.Start(ScheduleEntry.For(h.Workspace));

        RecordBuild(h, exitCode: 0);
        h.Launcher.Runs.Single().Run.Complete(0);
        Assert.IsTrue(SpinWait.SpinUntil(() => h.Launcher.Runs.Count == 2, Limit));
        h.Launcher.Runs[1].Run.Complete(0);

        Assert.IsTrue(SpinWait.SpinUntil(() => h.Failures.Count == 1, Limit));
        Assert.AreEqual("Stopped an afterRun loop: then-flow → then-build → then-flow.", h.Failures[0].Message);
        Assert.AreEqual("then-flow", h.Failures[0].Entry.Schedule.Id);
        Assert.HasCount(2, h.Launcher.Runs);
    }

    private static void RecordBuild(Harness h, int exitCode) => h.History.Add(new RunRecord
    {
        NodeKey = "Workspace:id:build",
        Name = "build",
        StartedAt = h.Time.GetLocalNow(),
        Outcome = RunOutcome.Exited,
        ExitCode = exitCode,
    }, []);
}
