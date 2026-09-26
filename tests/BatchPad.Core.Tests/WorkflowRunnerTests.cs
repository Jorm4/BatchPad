using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class WorkflowRunnerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    private const string Echo = """
        { "id": "echo", "path": "echo_args.bat",
          "params": [ { "name": "x", "type": "text" }, { "name": "end", "type": "flag", "arg": "--end", "default": true, "position": "end" } ] }
        """;

    private const string FailOn = """
        { "id": "fail-on", "path": "fail_on.bat", "params": [ { "name": "x", "type": "text" } ] }
        """;

    [TestMethod]
    public async Task FailingBuildSkipsRunButStillRunsAnAlwaysReport()
    {
        using var test = new WorkflowTest($$"""
            {{Echo}}, { "id": "build", "path": "exit3.bat" },
            { "id": "flow", "steps": [
                { "id": "build", "run": "build" },
                { "id": "run", "run": "echo" },
                { "id": "report", "run": "echo", "when": "always", "values": { "x": "${workflow.name}: ${workflow.result}" } } ] }
            """);

        var run = test.Start("flow");
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(WorkflowOutcome.Failed, result.Outcome);
        CollectionAssert.AreEqual(new[] { StepStatus.Failed, StepStatus.Skipped, StepStatus.Succeeded }, run.Steps.Select(s => s.Status).ToArray());
        Assert.AreEqual(3, run.Steps[0].Result!.ExitCode);
        CollectionAssert.AreEqual(new[] { "flow: failure" }, await test.OutputOf(run.Steps[2]));
    }

    [TestMethod]
    public async Task ForEachOverAnEmptyAllListRunsOncePerChoice()
    {
        using var test = new WorkflowTest($$"""
            {{Echo}},
            { "id": "flow", "params": [ { "name": "apps", "type": "multichoice", "choices": [ "A", "B", "C" ], "emptyMeans": "all" } ],
              "steps": [ { "id": "each", "forEach": "${param:apps}", "run": "echo", "values": { "x": "${item}" } } ] }
            """);

        var run = test.Start("flow", new() { ["apps"] = new JsonArray() });
        var result = await run.Completion.WaitAsync(Limit);

        Assert.IsTrue(result.Succeeded);
        var items = run.Steps[0].Items;
        CollectionAssert.AreEqual(new[] { "A", "B", "C" }, items.Select(i => i.Item).ToArray());
        foreach (var item in items)
            CollectionAssert.AreEqual(new[] { item.Item }, await test.OutputOf(item));
    }

    [TestMethod]
    public async Task OneFailingItemLeavesTheOthersRunningAndFailsTheWorkflow()
    {
        using var test = new WorkflowTest($$"""
            {{FailOn}},
            { "id": "flow", "params": [ { "name": "apps", "type": "multichoice" } ],
              "steps": [ { "id": "each", "forEach": "${param:apps}", "run": "fail-on", "values": { "x": "${item}" } } ] }
            """);

        var run = test.Start("flow", new() { ["apps"] = new JsonArray("one", "bad", "two") });
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(WorkflowOutcome.Failed, result.Outcome);
        Assert.AreEqual(StepStatus.Failed, run.Steps[0].Status);
        CollectionAssert.AreEqual(new[] { StepStatus.Succeeded, StepStatus.Failed, StepStatus.Succeeded },
            run.Steps[0].Items.Select(i => i.Status).ToArray());
    }

    [TestMethod]
    public async Task ListValueArrivesAsAListAndAnEmptyOneEmitsTheStepsEmptyArgs()
    {
        using var test = new WorkflowTest("""
            { "id": "build", "path": "echo_args.bat",
              "params": [ { "name": "apps", "type": "multichoice", "emptyArgs": [ "--everything" ] },
                          { "name": "end", "type": "flag", "arg": "--end", "default": true, "position": "end" } ] },
            { "id": "flow", "params": [ { "name": "apps", "type": "multichoice" }, { "name": "none", "type": "multichoice" } ],
              "steps": [
                { "id": "some", "run": "build" },
                { "id": "none", "run": "build", "values": { "apps": "${param:none}" }, "emptyArgs": [ "--tests" ] } ] }
            """);

        var run = test.Start("flow", new() { ["apps"] = new JsonArray("Bike Trials", "Rally"), ["none"] = new JsonArray() });
        Assert.IsTrue((await run.Completion.WaitAsync(Limit)).Succeeded);

        Assert.IsInstanceOfType<JsonArray>(run.Steps[0].Request!.Values!["apps"]);
        CollectionAssert.AreEqual(new[] { "Bike Trials", "Rally" }, await test.OutputOf(run.Steps[0]));
        Assert.IsInstanceOfType<JsonArray>(run.Steps[1].Request!.Values!["apps"]);
        CollectionAssert.AreEqual(new[] { "--tests" }, await test.OutputOf(run.Steps[1]));
    }

    [TestMethod]
    public async Task ParametersFlowByNameIntoNestedWorkflows()
    {
        using var test = new WorkflowTest($$"""
            {{Echo}},
            { "id": "inner", "params": [ { "name": "x", "type": "text" } ], "steps": [ { "id": "say", "run": "echo" } ] },
            { "id": "outer", "params": [ { "name": "x", "type": "text", "default": "Kart" } ], "steps": [ { "id": "nested", "run": "inner" } ] }
            """);

        var run = test.Start("outer");
        Assert.IsTrue((await run.Completion.WaitAsync(Limit)).Succeeded);

        var say = run.Steps[0].Nested!.Steps[0];
        CollectionAssert.AreEqual(new[] { "Kart" }, await test.OutputOf(say));
    }

    [TestMethod]
    public async Task WorkflowEndingAtALongRunningServerSucceedsAtReadyWhileTheServerKeepsRunning()
    {
        using var test = new WorkflowTest($$"""
            {{Echo}},
            { "id": "server", "path": "fake_server.py", "longRunning": true,
              "ready": { "pattern": "Serving on (http://\\S+)", "open": "$1" },
              "params": [ { "name": "port", "type": "text", "arg": "--port" } ] },
            { "id": "flow", "params": [ { "name": "port", "type": "text" } ],
              "steps": [ { "id": "build", "run": "echo" }, { "id": "serve", "run": "server" } ] }
            """);
        var port = FreePort();

        var run = test.Start("flow", new() { ["port"] = port.ToString() });
        var serve = run.Steps[1];
        try
        {
            var result = await run.Completion.WaitAsync(Limit);

            Assert.IsTrue(result.Succeeded);
            Assert.AreEqual(StepStatus.Ready, serve.Status);
            Assert.AreEqual($"http://127.0.0.1:{port}/", serve.ReadySignal!.Url);
            CollectionAssert.AreEqual(new[] { serve.ReadySignal.Url }, test.Opener.Targets.ToArray());
            Assert.IsFalse(serve.Handle!.Completion.IsCompleted, "the server stopped with the workflow");
        }
        finally
        {
            if (serve.Handle is { } handle)
                await handle.StopAsync(RunOutcome.Stopped, TimeSpan.Zero);
        }
    }

    [TestMethod]
    public void SelfReferencingWorkflowIsALoadError()
    {
        using var test = new WorkflowTest("""
            { "id": "loop", "name": "Loop", "steps": [ { "id": "again", "run": "workspace:loop" } ] }
            """, expectErrors: true);

        var error = test.Workspace.Workspace.Errors.Single();
        StringAssert.Contains(error.Message, "Loop → Loop");
        Assert.ThrowsExactly<WorkflowException>(() => test.Start("loop"));
    }

    [TestMethod]
    public async Task AStepResolvesItsTargetsArtifactsAndOpensThemPerPolicy()
    {
        using var test = new WorkflowTest("""
            { "id": "report", "path": "echo_args.bat", "args": [ "--end" ], "params": [ { "name": "x", "type": "text" } ],
              "artifacts": [ { "path": "${param:x}.html", "open": "onSuccess" } ] },
            { "id": "broken", "path": "exit3.bat", "artifacts": [ { "path": "broken.html", "open": "onSuccess" } ] },
            { "id": "flow", "steps": [
                { "id": "report", "run": "report", "values": { "x": "qa" } },
                { "id": "broken", "run": "broken" } ] }
            """);
        var report = test.Workspace.Temp.Path("qa.html");
        File.WriteAllText(report, "");
        File.WriteAllText(test.Workspace.Temp.Path("broken.html"), "");

        var run = test.Start("flow");
        await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(report, run.Steps[0].Artifacts.Single().Path);
        Assert.AreEqual(StepStatus.Failed, run.Steps[1].Status);
        CollectionAssert.AreEqual(new[] { report }, test.Opener.Targets.ToArray());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class WorkflowTest : IDisposable
    {
        public WorkflowTest(string scripts, bool expectErrors = false)
        {
            Workspace = new RunWorkspace(scripts, "echo_args.bat", "exit3.bat", "fake_server.py");
            File.WriteAllText(Workspace.Temp.Path("fail_on.bat"), "@echo off\r\nif \"%~1\"==\"bad\" exit /b 1\r\necho %~1\r\n");
            if (!expectErrors)
                Assert.IsEmpty(Workspace.Workspace.Errors, string.Join('\n', Workspace.Workspace.Errors));
            Launcher = new RecordingLauncher(new GatedStepLauncher(Workspace.Gate, RunWorkspace.Interpreters));
        }

        public RunWorkspace Workspace { get; }
        public RecordingLauncher Launcher { get; }
        public FakeOpener Opener { get; } = new();

        public WorkflowRun Start(string id, Dictionary<string, JsonNode?>? values = null)
        {
            var loaded = Workspace.Workspace;
            var workflow = (WorkflowNode)loaded.References.Resolve(id, TreeKind.Workspace)!;
            return new WorkflowRunner(loaded, Launcher, Opener).Start(new WorkflowRequest(loaded.Workspace, workflow) { Values = values });
        }

        public async Task<string[]> OutputOf(StepRun step)
        {
            var handle = step.Handle ?? throw new AssertFailedException($"Step {step.Id} {step.Item} did not start: {step.Error}");
            await handle.Completion.WaitAsync(Limit);
            return [.. handle.Output.Where(l => l.Stream == OutputStream.Stdout).Select(l => l.Text)];
        }

        public void Dispose()
        {
            foreach (var handle in Launcher.Started)
                handle.Dispose();
            Workspace.Dispose();
        }
    }

    private sealed class RecordingLauncher(IStepLauncher inner) : IStepLauncher
    {
        public ConcurrentQueue<RunHandle> Started { get; } = new();

        public RunHandle Start(RunRequest request)
        {
            var handle = inner.Start(request);
            Started.Enqueue(handle);
            return handle;
        }

        public Task StopAsync(RunHandle run, RunRequest request) => inner.StopAsync(run, request);
    }
}
