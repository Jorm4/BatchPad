using System.Diagnostics;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using Microsoft.Extensions.Time.Testing;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class ProcessRunnerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private static readonly InterpreterLocator Interpreters = new();
    private static readonly RunnerResolver Resolver = new(Interpreters);

    [TestMethod]
    public async Task PauseReturnsInsteadOfHanging()
    {
        using var run = Start(Script("pause.bat"));
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(RunOutcome.Exited, result.Outcome);
        Assert.AreEqual(0, result.ExitCode);
        CollectionAssert.Contains(Texts(run), "after");
    }

    [TestMethod]
    public async Task ExitCodeIsReported()
    {
        using var run = Start(Script("exit3.bat"));
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(3, result.ExitCode);
        Assert.IsFalse(result.Succeeded);
        Assert.IsGreaterThan(TimeSpan.Zero, result.Duration);
    }

    [TestMethod]
    public async Task RunTimesComeFromTheGivenClock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero));
        using var run = ProcessRunner.Start(Spec(Script("exit3.bat")), time);
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(time.GetLocalNow(), run.StartedAt);
        Assert.AreEqual(TimeSpan.Zero, result.Duration);
    }

    [TestMethod]
    public async Task OemAndUtf8LinesInOneRunBothDecode()
    {
        using var run = Start(Script("mixed_encoding.bat"), PythonOnPath());
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(0, result.ExitCode, string.Join('\n', Texts(run)));
        CollectionAssert.AreEqual(new[] { "OEM äÄö", "UTF-8 ä€" }, Texts(run));
    }

    [TestMethod]
    public async Task StopKillsADetachedChild()
    {
        using var run = Start(Script("spawn_detached.bat"), PythonOnPath());
        var pidLine = await FirstLineAsync(run);
        using var child = Process.GetProcessById(int.Parse(pidLine));

        await run.StopAsync().WaitAsync(Limit);

        Assert.IsTrue(child.WaitForExit(5000), "the detached child is still running");
        Assert.AreEqual(RunOutcome.Stopped, run.Completion.Result.Outcome);
    }

    [TestMethod]
    public async Task TimeoutKillsASleeper()
    {
        var spec = Spec(Script("sleep_forever.py")) with { Timeout = TimeSpan.FromSeconds(1) };
        using var run = ProcessRunner.Start(spec);
        using var sleeper = Process.GetProcessById(int.Parse(await FirstLineAsync(run)));

        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(RunOutcome.TimedOut, result.Outcome);
        Assert.IsTrue(sleeper.WaitForExit(5000));
    }

    [TestMethod]
    public void PathAndPATHMergeIntoOneVariable()
    {
        var environment = new EnvironmentBuilder([new("Path", @"C:\old")])
            .Apply(new Dictionary<string, string> { ["PATH"] = @"C:\new" })
            .Build();

        Assert.HasCount(1, environment);
        Assert.AreEqual(@"C:\new", environment["path"]);
    }

    [TestMethod]
    public async Task TheChildSeesTheLayeredPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH");
        var environment = EnvironmentBuilder.FromCurrentProcess()
            .Apply(new Dictionary<string, string> { ["PATH"] = @"C:\BatchPadMarker;" + current })
            .Build();
        var command = Resolver.Resolve(new ScriptNode { Command = "echo %PATH%" }, [], Fixtures.Path("run"));

        using var run = ProcessRunner.Start(new RunSpec(command, environment));
        await run.Completion.WaitAsync(Limit);

        StringAssert.StartsWith(Texts(run).Single(), @"C:\BatchPadMarker;");
    }

    [TestMethod]
    public void EnvironmentLayersApplyInOrderAndEnvFilesAreRead()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path(".env"), "# comment\nexport A=\"from file\"\nB=file\n");

        var environment = new EnvironmentBuilder([new("A", "base"), new("C", "base")])
            .ApplyFile(dir.Path(".env"))
            .Apply(new Dictionary<string, string> { ["b"] = "layer", ["C"] = "" })
            .Build();

        Assert.AreEqual("from file", environment["A"]);
        Assert.AreEqual("layer", environment["B"]);
        Assert.IsFalse(environment.ContainsKey("C"));
    }

    [TestMethod]
    public async Task APlannedDemoScriptRunsEndToEnd()
    {
        using var dir = new TempDir();
        var workspace = WorkspaceLoader.Load(Path.Combine(Fixtures.DemoWorkspace, "batchpad.json"), new AppPaths(dir.Root, false));
        var script = workspace.Workspace.AllNodes().Select(n => n.Node).OfType<ScriptNode>().Single(s => s.Id == "hello-bat");

        var specs = RunPlanner.Plan(new RunRequest(workspace, workspace.Workspace, script), Interpreters);
        using var run = ProcessRunner.Start(specs);
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.StartsWith(Texts(run).Single(), "Hello from batch!");
        Assert.AreEqual("utf-8", specs[0].Environment["PYTHONIOENCODING"]);
    }

    [TestMethod]
    public async Task WindowModeReportsTheExitCode()
    {
        using var run = ProcessRunner.Start(Spec(Script("exit3.bat")) with { Console = ConsoleMode.Window });
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(RunOutcome.Exited, result.Outcome);
        Assert.AreEqual(3, result.ExitCode);
    }

    [TestMethod]
    public void TheRequestConsoleOverridesTheScripts()
    {
        using var dir = new TempDir();
        var workspace = WorkspaceLoader.Load(Path.Combine(Fixtures.DemoWorkspace, "batchpad.json"), new AppPaths(dir.Root, false));
        var script = workspace.Workspace.AllNodes().Select(n => n.Node).OfType<ScriptNode>().Single(s => s.Id == "hello-bat");

        var specs = RunPlanner.Plan(new RunRequest(workspace, workspace.Workspace, script) { Console = ConsoleMode.Window }, Interpreters);

        Assert.AreEqual(ConsoleMode.Window, specs.Single().Console);
    }

    [TestMethod]
    public void DurationsParse()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(30), RunPlanner.ParseDuration("30m"));
        Assert.AreEqual(TimeSpan.FromMinutes(90), RunPlanner.ParseDuration("1h30m"));
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), RunPlanner.ParseDuration("500ms"));
        Assert.IsNull(RunPlanner.ParseDuration(null));
        Assert.ThrowsExactly<RunException>(() => RunPlanner.ParseDuration("soon"));
    }

    private static ScriptNode Script(string fileName) => new() { Path = Fixtures.Path("run", fileName) };

    private static Dictionary<string, string> PythonOnPath() => new()
    {
        ["PATH"] = Path.GetDirectoryName(Interpreters.Python()!.Path) + ";" + Environment.GetEnvironmentVariable("PATH"),
    };

    private static RunSpec Spec(ScriptNode script, Dictionary<string, string>? environment = null) =>
        new(Resolver.Resolve(script, [], Fixtures.Path("run")),
            EnvironmentBuilder.FromCurrentProcess()
                .Apply(new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" })
                .Apply(environment)
                .Build());

    private static RunHandle Start(ScriptNode script, Dictionary<string, string>? environment = null) =>
        ProcessRunner.Start(Spec(script, environment));

    private static List<string> Texts(RunHandle run) =>
        [.. run.Output.Where(l => l.Stream != OutputStream.Info).Select(l => l.Text)];

    private static async Task<string> FirstLineAsync(RunHandle run)
    {
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = run.Subscribe(line => first.TrySetResult(line.Text));
        try
        {
            return await first.Task.WaitAsync(Limit);
        }
        catch
        {
            await run.StopAsync(RunOutcome.Stopped, TimeSpan.Zero);
            throw;
        }
    }
}
