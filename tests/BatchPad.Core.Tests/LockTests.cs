using System.Diagnostics;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class LockTests
{
    private const string Scripts = """
        { "id": "a", "path": "nap.py", "lock": "x" },
        { "id": "b", "path": "nap.py", "lock": "x" },
        { "id": "other", "path": "nap.py", "lock": "y" },
        { "id": "single", "path": "nap.py", "singleInstance": true },
        { "id": "flow", "lock": "x", "steps": [ { "run": "a" }, { "run": "b" } ] },
        { "id": "plain", "path": "nap.py" },
        { "id": "x-then-y", "lock": "x", "steps": [ { "run": "plain" }, { "run": "other" } ] },
        { "id": "y-then-x", "lock": "y", "steps": [ { "run": "plain" }, { "run": "a" } ] }
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
        Assert.IsFalse(test.IsHeld("x"));
    }

    [TestMethod]
    public async Task AWorkflowsLockQueuesAStandaloneRun()
    {
        using var test = new LockTest();
        var loaded = test.Workspace.Workspace;
        var workflow = (WorkflowNode)loaded.References.Resolve("flow", TreeKind.Workspace)!;
        var run = new WorkflowRunner(loaded, test.Workspace.Gate, RunWorkspace.Interpreters)
            .Start(new WorkflowRequest(loaded.Workspace, workflow));
        await Eventually(() => test.IsHeld("x"));

        using var lone = test.Start("a");
        Assert.AreEqual("x", lone.WaitingForLock);
        await run.Completion.WaitAsync(Limit);
        await lone.Completion.WaitAsync(Limit);
        Assert.IsGreaterThanOrEqualTo(EndOf(run.Steps[1].Handle!), lone.StartedAt);
    }

    [TestMethod]
    public async Task WorkflowsTakingEachOthersStepLocksDoNotDeadlock()
    {
        using var test = new LockTest();
        var loaded = test.Workspace.Workspace;
        var runner = new WorkflowRunner(loaded, test.Workspace.Gate, RunWorkspace.Interpreters);
        WorkflowRun Start(string id) => runner.Start(new WorkflowRequest(loaded.Workspace, (WorkflowNode)loaded.References.Resolve(id, TreeKind.Workspace)!));

        var first = Start("x-then-y");
        var second = Start("y-then-x");
        var results = await Task.WhenAll(first.Completion, second.Completion).WaitAsync(Limit);

        Assert.IsTrue(results.All(r => r.Succeeded));
        Assert.IsFalse(test.IsHeld("x") || test.IsHeld("y"));
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
        Assert.IsFalse(test.IsHeld("x"));
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

    [TestMethod]
    public async Task ManagersSharingADirectorySerialiseTheSameLockButNotDifferentOnes()
    {
        using var temp = new TempDir();
        var first = new LockManager(temp.Root);
        var second = new LockManager(temp.Root);

        var held = await first.AcquireAsync(["x"], new object());
        var queued = second.AcquireAsync(["X"], new object());
        using (await second.AcquireAsync(["y"], new object()).WaitAsync(Limit))
            await Task.Delay(100);
        Assert.IsFalse(queued.IsCompleted);

        held.Dispose();
        (await queued.WaitAsync(Limit)).Dispose();
    }

    [TestMethod]
    public async Task ALockHeldByAProcessThatDiesIsFreed()
    {
        using var temp = new TempDir();
        var locks = new LockManager(temp.Root);
        var python = new InterpreterLocator().Python() ?? throw new AssertInconclusiveException("Python is not installed.");
        var startInfo = new ProcessStartInfo(python.Path) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in python.LeadingArguments.Concat(["-c",
                     "import os, sys, time; f = open(sys.argv[1], 'a'); print(os.getpid(), flush=True); time.sleep(60)",
                     MachineLock.PathFor(temp.Root, "x")]))
            startInfo.ArgumentList.Add(argument);
        using var launcher = Process.Start(startInfo)!;
        using var holder = Process.GetProcessById(int.Parse(launcher.StandardOutput.ReadLine()!));

        Assert.ThrowsExactly<LockBusyException>(() => locks.AcquireAsync(["x"], new object(), wait: false).GetAwaiter().GetResult());
        var queued = locks.AcquireAsync(["x"], new object());
        holder.Kill();
        launcher.Kill();

        (await queued.WaitAsync(Limit)).Dispose();
    }

    [TestMethod]
    public async Task AWaiterInAnotherProcessIsToldWhoHoldsTheLock()
    {
        using var temp = new TempDir();
        using var held = await new LockManager(temp.Root).AcquireAsync(["x"], new object(), holder: "Build");
        var other = new LockManager(temp.Root);
        var reported = new List<string>();
        using var stop = new CancellationTokenSource();

        var queued = other.AcquireAsync(["x"], new object(), text => { lock (reported) reported.Add(text); }, stop.Token);
        await Eventually(() => { lock (reported) return reported.Count > 0; });

        StringAssert.Matches(reported[0], new System.Text.RegularExpressions.Regex(@"^x \(held by Build since \d\d:\d\d\)$"));
        StringAssert.StartsWith(other.DescribeHolder("x"), "held by Build since ");
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.IsFalse(other.IsHeld("x"));
    }

    [TestMethod]
    public async Task ANoWaitAcquireFailsFastWhereverTheLockIsHeld()
    {
        using var temp = new TempDir();
        var locks = new LockManager(temp.Root);
        using (await locks.AcquireAsync(["x"], new object(), holder: "Build"))
        {
            var here = locks.AcquireAsync(["x"], new object(), wait: false);
            var elsewhere = new LockManager(temp.Root).AcquireAsync(["x"], new object(), wait: false);

            Assert.IsTrue(here.IsFaulted && elsewhere.IsFaulted);
            StringAssert.Contains(here.Exception!.InnerException!.Message, "held by Build");
            StringAssert.Contains(elsewhere.Exception!.InnerException!.Message, "held by Build");
        }
        (await locks.AcquireAsync(["x"], new object(), wait: false)).Dispose();
    }

    [TestMethod]
    public async Task ARunThatMustNotWaitIsRefusedWhileItsLockIsHeld()
    {
        using var test = new LockTest();
        using var first = test.Start("a");

        Assert.ThrowsExactly<LockBusyException>(() => test.Workspace.Gate.Start(test.Workspace.Request("b"), RunWorkspace.Interpreters, waitForLocks: false));
        await first.Completion.WaitAsync(Limit);
    }

    private static DateTimeOffset EndOf(RunHandle run) => run.StartedAt + run.Completion.Result.Duration;

    private sealed class LockTest : IDisposable
    {
        public LockTest()
        {
            Workspace = new RunWorkspace(Scripts);
            File.WriteAllText(Workspace.Temp.Path("nap.py"), "import time\ntime.sleep(0.2)\n");
            Assert.IsEmpty(Workspace.Workspace.Errors, string.Join('\n', Workspace.Workspace.Errors));
        }

        public RunWorkspace Workspace { get; }

        public RunHandle Start(string id) => Workspace.Gate.Start(Workspace.Request(id), RunWorkspace.Interpreters);

        public bool IsHeld(string name) => Workspace.Gate.Locks.IsHeld(LockKeys.For(name, null, Workspace.Workspace.CheckoutDirectory));

        public void Dispose() => Workspace.Dispose();
    }
}
