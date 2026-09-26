using System.Diagnostics;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class RunRecordTests
{
    [TestMethod]
    public async Task AWorkflowsStepsShareTheWorkflowRecordAsTheirParent()
    {
        using var workspace = new RunWorkspace("""
            { "id": "one", "path": "ok.bat" },
            { "id": "two", "path": "ok.bat" },
            { "folder": "Build", "items": [ { "id": "flow", "tags": ["ci"], "steps": [ { "id": "first", "run": "one" }, { "id": "second", "run": "two" } ] } ] }
            """);
        File.WriteAllText(workspace.Temp.Path("ok.bat"), "@echo ok\r\n");
        var loaded = workspace.Workspace;
        var workflow = (WorkflowNode)loaded.References.Resolve("flow", TreeKind.Workspace)!;
        var store = new HistoryStore(workspace.Temp.Path("history"));
        var run = new WorkflowRunner(loaded, workspace.Gate, RunWorkspace.Interpreters).Start(new WorkflowRequest(loaded.Workspace, workflow));

        var record = await HistoryRecorder.AttachWorkflow(run, store, new RunRecord { NodeKey = "Workspace:id:flow", Name = "flow" },
            request => request.Script.Id!, RunTriggers.Manual).WaitAsync(Limit);

        var steps = store.Recent().Where(r => r.Id != record.Id).ToList();
        Assert.AreEqual(run.RunId, record.Id);
        Assert.IsNull(record.ParentRunId);
        Assert.AreEqual("Build", record.Folder);
        CollectionAssert.AreEqual(new[] { "ci" }, record.Tags);
        Assert.HasCount(2, steps);
        Assert.IsTrue(steps.All(s => s.ParentRunId == record.Id));
        CollectionAssert.AreEquivalent(new[] { "first", "second" }, steps.Select(s => s.StepId).ToList());
        Assert.AreEqual(Environment.MachineName, record.Machine);
        Assert.IsNotNull(record.BatchPadVersion);
    }

    [TestMethod]
    public async Task ARunThatWaitedForALockRecordsTheWaitApartFromItsDuration()
    {
        using var workspace = new RunWorkspace("""
            { "id": "a", "path": "nap.py", "lock": "x" },
            { "id": "b", "path": "nap.py", "lock": "x" }
            """);
        File.WriteAllText(workspace.Temp.Path("nap.py"), "import time\ntime.sleep(0.3)\n");
        var store = new HistoryStore(workspace.Temp.Path("history"));
        using var first = workspace.Gate.Start(workspace.Request("a"), RunWorkspace.Interpreters);
        var clock = Stopwatch.StartNew();
        var request = workspace.Request("b");
        using var second = workspace.Gate.Start(request, RunWorkspace.Interpreters);

        var record = await HistoryRecorder.Attach(second, store, request, "Workspace:id:b").WaitAsync(Limit);
        var total = clock.Elapsed;

        Assert.IsGreaterThan(100, record.QueuedMs);
        Assert.IsLessThanOrEqualTo(total - TimeSpan.FromMilliseconds(record.QueuedMs) + TimeSpan.FromMilliseconds(50), record.Duration);
        Assert.AreEqual(second.StartedAt, record.StartedAt);
    }

    [TestMethod]
    public async Task ARunWithATestReportRecordsCountsFailedNamesAndSlowestCases()
    {
        using var workspace = new RunWorkspace("""{ "id": "tests", "path": "report.bat", "testReport": "report.xml" }""");
        File.Copy(Fixtures.Path("junit", "pytest.xml"), workspace.Temp.Path("pytest.xml"));
        File.WriteAllText(workspace.Temp.Path("report.bat"), "@type \"%~dp0pytest.xml\" > \"%~dp0report.xml\"\r\n");

        var record = await Record(workspace, "tests");

        var tests = record.Tests!;
        Assert.AreEqual((2, 2, 1), (tests.Passed, tests.Failed, tests.Skipped));
        CollectionAssert.AreEqual(new[] { "tests.test_math.test_divide", "tests.test_io.test_write" }, tests.FailedNames);
        Assert.AreEqual(new TestTiming("tests.test_io.test_write", 0.010), tests.Slowest[0]);
        Assert.HasCount(4, tests.Slowest);
    }

    [TestMethod]
    public async Task AnErrorPatternLineIsRecordedWithItsSourceLocation()
    {
        using var workspace = new RunWorkspace("""{ "id": "build", "path": "build.bat", "errorPatterns": [ ": error " ] }""");
        Directory.CreateDirectory(workspace.Temp.Path("src"));
        File.WriteAllText(workspace.Temp.Path("src", "main.c"), "");
        File.WriteAllText(workspace.Temp.Path("build.bat"), "@echo compiling\r\n@echo src\\main.c(12,5): error C2065: undeclared\r\n");

        var record = await Record(workspace, "build");

        var error = record.Errors!.Single();
        Assert.AreEqual(@"src\main.c(12,5): error C2065: undeclared", error.Text);
        Assert.AreEqual((workspace.Temp.Path("src", "main.c"), 12, 5), (error.File, error.Line, error.Column));
    }

    [TestMethod]
    public void ARecordAnotherStoreWritesIsAnnouncedAndListed()
    {
        using var dir = new TempDir();
        var app = new HistoryStore(dir.Root);
        Assert.IsEmpty(app.Recent());
        var announced = new List<RunRecord>();
        app.RunRecorded += record =>
        {
            lock (announced)
                announced.Add(record);
        };

        var written = new HistoryStore(dir.Root).Add(new RunRecord { NodeKey = "Workspace:id:build", Name = "build", StartedAt = DateTimeOffset.Now }, ["ok"]);

        Assert.IsTrue(SpinWait.SpinUntil(() =>
        {
            lock (announced)
                return announced.Count > 0;
        }, Limit));
        lock (announced)
            Assert.AreEqual(written.Id, announced.Single().Id);
        Assert.AreEqual(written.Id, app.Recent().Single().Id);
    }

    [TestMethod]
    public void ARecordWrittenBeforeTheNewFieldsStillLoads()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Root, "20260301-090000-000-abcdef12.json"), """
            {
              "id": "20260301-090000-000-abcdef12", "nodeKey": "Workspace:id:build", "tree": "workspace", "nodeId": "build",
              "path": "build.bat", "name": "Build", "command": "build.bat", "values": { "mode": "fast" }, "extraArguments": null,
              "trigger": "schedule:sched:nightly", "startedAt": "2026-03-01T09:00:00+00:00", "duration": "00:00:01.5000000",
              "outcome": "exited", "exitCode": 0
            }
            """);

        var record = new HistoryStore(dir.Root, maxAge: TimeSpan.FromDays(100_000)).Recent().Single();

        Assert.AreEqual("Build", record.Name);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), record.Duration);
        Assert.AreEqual("sched:nightly", RunTriggers.ScheduleKey(record.Trigger));
        Assert.IsNull(record.ParentRunId);
        Assert.AreEqual(0, record.QueuedMs);
        Assert.IsNull(record.Tests);
    }

    private static async Task<RunRecord> Record(RunWorkspace workspace, string id)
    {
        var store = new HistoryStore(workspace.Temp.Path("history"));
        var request = workspace.Request(id);
        using var run = workspace.Gate.Start(request, RunWorkspace.Interpreters);
        return await HistoryRecorder.Attach(run, store, request, "Workspace:id:" + id).WaitAsync(Limit);
    }
}
