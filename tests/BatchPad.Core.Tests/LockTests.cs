using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class LockTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private const string Scripts = """
        { "id": "a", "path": "nap.py", "lock": "x" },
        { "id": "b", "path": "nap.py", "lock": "x" },
        { "id": "other", "path": "nap.py", "lock": "y" },
        { "id": "single", "path": "nap.py", "singleInstance": true },
        { "id": "flow", "lock": "x", "steps": [ { "run": "a" }, { "run": "b" } ] }
        """;

    [TestMethod]
    public async Task RunsSharingALockNeverOverlap()
    {
        using var test = new LockTest();
        using var first = test.Start("a");
        using var second = test.Start("b");

        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(Limit);

        Assert.IsTrue(first.Completion.Result.Succeeded && second.Completion.Result.Succeeded);
        Assert.IsGreaterThanOrEqualTo(EndOf(first), second.StartedAt);
    }

    [TestMethod]
    public async Task DifferentLocksRunConcurrently()
    {
        using var test = new LockTest();
        using var first = test.Start("a");
        using var second = test.Start("other");

        Assert.IsNull(second.WaitingForLock);
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(Limit);
        Assert.IsLessThan(EndOf(first), second.StartedAt);
    }

    [TestMethod]
    public async Task AWorkflowHoldingALockRunsStepsThatTakeIt()
    {
        using var test = new LockTest();
        var loaded = test.Workspace.Workspace;
        var workflow = (WorkflowNode)loaded.References.Resolve("flow", TreeKind.Workspace)!;
        var runner = new WorkflowRunner(loaded, test.Workspace.Gate, RunWorkspace.Interpreters);

        var run = runner.Start(new WorkflowRequest(loaded.Workspace, workflow));
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(WorkflowOutcome.Succeeded, result.Outcome, string.Join('\n', run.Steps.Select(s => s.Error)));
        Assert.IsFalse(test.Workspace.Gate.Locks.IsHeld("x"));
    }

    [TestMethod]
    public async Task AWorkflowsLockQueuesAStandaloneRun()
    {
        using var test = new LockTest();
        var loaded = test.Workspace.Workspace;
        var workflow = (WorkflowNode)loaded.References.Resolve("flow", TreeKind.Workspace)!;
        var run = new WorkflowRunner(loaded, test.Workspace.Gate, RunWorkspace.Interpreters)
            .Start(new WorkflowRequest(loaded.Workspace, workflow));
        while (!test.Workspace.Gate.Locks.IsHeld("x"))
            await Task.Yield();

        using var lone = test.Start("a");
        Assert.AreEqual("x", lone.WaitingForLock);
        await run.Completion.WaitAsync(Limit);
        await lone.Completion.WaitAsync(Limit);
        Assert.IsGreaterThanOrEqualTo(EndOf(run.Steps[1].Handle!), lone.StartedAt);
    }

    [TestMethod]
    public async Task SingleInstanceQueuesASecondRun()
    {
        using var test = new LockTest();
        using var first = test.Start("single");
        using var second = test.Start("single");

        Assert.IsNotNull(second.WaitingForLock);
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(Limit);
        Assert.IsGreaterThanOrEqualTo(EndOf(first), second.StartedAt);
    }

    [TestMethod]
    public async Task AQueuedRunReportsWhatItIsWaitingFor()
    {
        using var test = new LockTest();
        using var first = test.Start("a");
        using var second = test.Start("b");
        var started = new TaskCompletionSource();
        second.WaitingChanged += () => started.TrySetResult();

        Assert.AreEqual("x", second.WaitingForLock);
        Assert.IsTrue(second.Output.Any(l => l.Text.Contains("Waiting for lock 'x'")));
        await started.Task.WaitAsync(Limit);
        Assert.IsNull(second.WaitingForLock);
        await second.Completion.WaitAsync(Limit);
    }

    [TestMethod]
    public async Task StoppingAQueuedRunNeverStartsIt()
    {
        using var test = new LockTest();
        using var first = test.Start("a");
        using var second = test.Start("b");

        await second.StopAsync().WaitAsync(Limit);

        Assert.AreEqual(RunOutcome.Stopped, second.Completion.Result.Outcome);
        Assert.AreEqual(default, second.StartedAt);
        await first.Completion.WaitAsync(Limit);
        Assert.IsFalse(test.Workspace.Gate.Locks.IsHeld("x"));
    }

    [TestMethod]
    public async Task LocksAreQueuedFirstInFirstOut()
    {
        var locks = new LockManager();
        var order = new List<int>();
        var holder = await locks.AcquireAsync(["x"], new object());
        var waiters = Enumerable.Range(0, 3).Select(async i =>
        {
            using var lease = await locks.AcquireAsync(["x"], new object());
            order.Add(i);
        }).ToList();

        holder.Dispose();
        await Task.WhenAll(waiters).WaitAsync(Limit);

        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, order);
        Assert.IsFalse(locks.IsHeld("x"));
    }

    private static DateTimeOffset EndOf(RunHandle run) => run.StartedAt + run.Completion.Result.Duration;

    private sealed class LockTest : IDisposable
    {
        public LockTest()
        {
            Workspace = new RunWorkspace(Scripts);
            File.WriteAllText(Workspace.Temp.Path("nap.py"), "import time\ntime.sleep(0.4)\n");
            Assert.IsEmpty(Workspace.Workspace.Errors, string.Join('\n', Workspace.Workspace.Errors));
        }

        public RunWorkspace Workspace { get; }

        public RunHandle Start(string id) => Workspace.Gate.Start(Workspace.Request(id), RunWorkspace.Interpreters);

        public void Dispose() => Workspace.Dispose();
    }
}
