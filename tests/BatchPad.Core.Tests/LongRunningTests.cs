using System.Text.Json.Nodes;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class LongRunningTests
{
    private const string Server = """
        { "id": "server", "path": "fake_server.py", "longRunning": true, "stop": "stop-server",
          "ready": { "pattern": "Serving on (http://\\S+)", "open": "$1${param:page}" },
          "params": [ { "name": "port", "type": "text", "arg": "--port" }, { "name": "page", "type": "text", "default": "index.html", "emit": false } ] },
        { "id": "stop-server", "path": "stop_fake_server.py", "params": [ { "name": "port", "type": "text", "arg": "--port" } ] }
        """;

    [TestMethod]
    public async Task ReadyFiresWithTheExpandedUrl()
    {
        using var workspace = new RunWorkspace(Server, "fake_server.py", "stop_fake_server.py");
        var port = FreePort();
        var request = workspace.Request("server", new() { ["port"] = port.ToString() });
        var opener = new FakeOpener();
        using var run = workspace.Gate.Start(request, RunWorkspace.Interpreters);
        try
        {
            using var watcher = ReadyWatcher.Watch(run, request, opener);
            var signal = await watcher.Ready.WaitAsync(Limit);

            Assert.AreEqual($"http://127.0.0.1:{port}/index.html", signal!.Url);
            await opener.FirstOpen.WaitAsync(Limit);
            CollectionAssert.AreEqual(new[] { signal.Url }, opener.Targets.ToArray());
        }
        finally
        {
            await run.StopAsync(RunOutcome.Stopped, TimeSpan.Zero);
        }
    }

    [TestMethod]
    public async Task StopRunsTheCompanionWithTheSamePortAndTheServerEnds()
    {
        using var workspace = new RunWorkspace(Server, "fake_server.py", "stop_fake_server.py");
        var port = FreePort();
        var request = workspace.Request("server", new() { ["port"] = port.ToString() });
        using var run = workspace.Gate.Start(request, RunWorkspace.Interpreters);
        try
        {
            using var watcher = ReadyWatcher.Watch(run, request);
            await watcher.Ready.WaitAsync(Limit);

            using var companion = await new StopCoordinator(workspace.Gate, RunWorkspace.Interpreters).StopAsync(run, request).WaitAsync(Limit);

            Assert.IsNotNull(companion);
            Assert.IsTrue((await companion.Completion.WaitAsync(Limit)).Succeeded, string.Join('\n', companion.Output));
            CollectionAssert.Contains(companion.Output.Select(l => l.Text).ToList(), $"Asked port {port} to stop.");
            Assert.AreEqual(RunOutcome.Stopped, run.Completion.Result.Outcome);
            CollectionAssert.Contains(run.Output.Select(l => l.Text).ToList(), "Stopped.", "the server ended itself, not by termination");
        }
        finally
        {
            await run.StopAsync(RunOutcome.Stopped, TimeSpan.Zero);
        }
    }

    [TestMethod]
    public void ExeWorksInItsOwnFolderAndAMissingOneIsNotBuiltYet()
    {
        using var workspace = new RunWorkspace("""
            { "id": "game", "path": "build/${param:config}/bin/game.exe", "longRunning": true,
              "params": [ { "name": "config", "type": "text", "default": "release" } ] }
            """);

        var missing = Assert.ThrowsExactly<RunException>(() => workspace.Gate.Start(workspace.Request("game"), RunWorkspace.Interpreters));
        StringAssert.Contains(missing.Message, "not built yet");

        var exe = workspace.Temp.Path("build", "release", "bin", "game.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllBytes(exe, []);
        var spec = RunPlanner.Plan(workspace.Request("game"), RunWorkspace.Interpreters).Single();
        Assert.AreEqual(Path.GetDirectoryName(exe), spec.Command.WorkingDirectory);
    }

    [TestMethod]
    public async Task OnSuccessArtifactOpensOnlyAfterExitZero()
    {
        using var workspace = new RunWorkspace("""
            { "id": "ok", "path": "echo_args.bat", "args": [ "--end" ], "artifacts": [ { "path": "${param:name}.html", "open": "onSuccess" } ],
              "params": [ { "name": "name", "type": "text", "default": "report", "emit": false } ] },
            { "id": "fails", "path": "exit3.bat", "artifacts": [ { "path": "report.html", "open": "onSuccess" } ] }
            """, "echo_args.bat", "exit3.bat");
        File.WriteAllText(workspace.Temp.Path("report.html"), "<p>report</p>");

        var opener = new FakeOpener();
        foreach (var id in new[] { "fails", "ok" })
        {
            var request = workspace.Request(id);
            var artifacts = ArtifactResolver.Resolve(request);
            using var run = workspace.Gate.Start(request, RunWorkspace.Interpreters);
            var result = await run.Completion.WaitAsync(Limit);
            ArtifactResolver.OpenAfter(result, artifacts, opener);
            if (id == "fails")
                Assert.IsEmpty(opener.Targets);
        }

        CollectionAssert.AreEqual(new[] { workspace.Temp.Path("report.html") }, opener.Targets.ToArray());
    }
}

/// <summary>A trusted temporary workspace whose <c>batchpad.json</c> holds the given script entries, with run fixtures copied in.</summary>
internal sealed class RunWorkspace : IDisposable
{
    public static readonly InterpreterLocator Interpreters = new();

    public RunWorkspace(string scripts, params string[] fixtures)
    {
        foreach (var fixture in fixtures)
            File.Copy(Fixtures.Path("run", fixture), Temp.Path(fixture));
        File.WriteAllText(Temp.Path("batchpad.json"), $$"""{ "id": "run-test", "scripts": [ {{scripts}} ] }""");
        var paths = new AppPaths(Temp.Path("data"));
        Workspace = WorkspaceLoader.Load(Temp.Path("batchpad.json"), paths);
        var trust = TrustStore.Load(paths);
        trust.Trust(Temp.Root);
        Gate = new RunGate(trust);
    }

    public TempDir Temp { get; } = new();
    public LoadedWorkspace Workspace { get; }
    public RunGate Gate { get; }

    public RunRequest Request(string id, Dictionary<string, JsonNode?>? values = null) =>
        new(Workspace, Workspace.Workspace, (ScriptNode)Workspace.References.Resolve(id, TreeKind.Workspace)!) { Values = values };

    public void Dispose() => Temp.Dispose();
}
