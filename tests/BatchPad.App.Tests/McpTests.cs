using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.App.Cli;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class McpTests
{
    [TestMethod]
    public void TheParserRefusesAWorkspaceForMcp()
    {
        Assert.AreEqual(CliVerb.Mcp, CliCommand.Parse(["mcp"]).Verb);
        Assert.IsTrue(CliCommand.IsCli(["mcp"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["mcp", "--json"]));
        StringAssert.Contains(Assert.Throws<CliUsageException>(() => CliCommand.Parse(["mcp", "--workspace", "repo"])).Message, "'directory'");
    }

    [TestMethod]
    public async Task AnUntrustedDirectoryRefusesEveryTool()
    {
        using var test = new TestWorkspace();
        var directory = Path.GetDirectoryName(WriteWorkspace(test))!;
        await using var tools = EnabledTools(test);

        foreach (var result in new[]
                 {
                     await tools.ListScripts(directory), await tools.GetStats(Path.Combine(directory, "src")),
                     await tools.GetLog(directory, "any"), await tools.GetLog(directory, @"..\..\settings"),
                 })
        {
            Assert.IsTrue(result.IsError);
            StringAssert.Contains(((TextContentBlock)result.Content.Single()).Text, "until you trust this workspace folder");
        }
    }

    [TestMethod]
    public async Task OnlyALocalDriveDirectoryIsAccepted()
    {
        using var test = new TestWorkspace();
        await using var tools = EnabledTools(test);

        foreach (var directory in new[] { @"\\host\share\repo", "//host/share/repo", @"\\?\C:\repo", @"\\.\C:\repo", @"C:repo", @"repo", "" })
        {
            var refusal = await Assert.ThrowsAsync<McpException>(() => tools.ListScripts(directory));
            StringAssert.Contains(refusal.Message, "local drive", directory);
        }
    }

    [TestMethod]
    public async Task TheCheckoutReportedIsTheWorkspacesNotTheDirectorysOwn()
    {
        using var repo = GitRepo.WithWhichScript();
        using var test = new TestWorkspace();
        new TrustStore(new Settings(), test.Paths.SettingsFile).Trust(repo.Main);
        var nested = Directory.CreateDirectory(Path.Combine(repo.Main, "vendor", "lib")).FullName;
        GitRepo.Git(nested, "init", "-q", "-b", "trunk");
        await using var tools = EnabledTools(test);

        var listed = JsonNode.Parse(((TextContentBlock)(await tools.ListScripts(nested)).Content[0]).Text)!;

        Assert.AreEqual("repo", Path.GetFileName(listed["checkout"]!["directory"]!.GetValue<string>()));
        Assert.AreEqual("main", listed["checkout"]!["branch"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AServerStartedInTheMainCheckoutRunsTheScriptsOfTheDirectoryItIsGiven()
    {
        using var repo = GitRepo.WithWhichScript();
        using var test = new TestWorkspace();
        new TrustStore(new Settings(), test.Paths.SettingsFile).Trust(repo.Main);
        await using var client = await McpClient.StartInitializedAsync(test, repo.Main);

        var worktree = await client.RunScriptAsync(repo.Worktree, "which");
        Assert.AreEqual("worktree", worktree["checkout"]!["kind"]!.GetValue<string>());
        Assert.AreEqual(GitRepo.WorktreeBranch, worktree["checkout"]!["branch"]!.GetValue<string>());
        StringAssert.Contains(File.ReadAllText(worktree["logPath"]!.GetValue<string>()), "worktree");

        var main = await client.RunScriptAsync(repo.Main, "which");
        Assert.AreEqual("main", main["checkout"]!["kind"]!.GetValue<string>());
        Assert.AreEqual("main", File.ReadAllText(main["logPath"]!.GetValue<string>()).Trim());

        var listed = JsonNode.Parse(TextOf(await client.CallToolAsync("list_scripts", new JsonObject { ["directory"] = repo.Worktree })))!;
        Assert.AreEqual("worktree", listed["checkout"]!["kind"]!.GetValue<string>());
        Assert.AreEqual("which", listed["scripts"]![0]!["id"]!.GetValue<string>());

        var withoutDirectory = await client.RequestAsync("tools/call", new JsonObject
        {
            ["name"] = "run_script",
            ["arguments"] = new JsonObject { ["id"] = "which" },
        });
        Assert.IsTrue(withoutDirectory["error"] is not null || withoutDirectory["result"]!["isError"]?.GetValue<bool>() == true,
            withoutDirectory.ToJsonString());

        var untrusted = await client.CallToolAsync("run_script",
            new JsonObject { ["directory"] = Path.GetDirectoryName(WriteWorkspace(test))!, ["id"] = "pass" });
        Assert.IsTrue(untrusted["isError"]!.GetValue<bool>());
        StringAssert.Contains(TextOf(untrusted), "until you trust this workspace folder");
        Assert.HasCount(2, HistoryStore.For(test.Paths, GitRepo.WorkspaceId).Recent());

        Assert.AreEqual(0, await client.CloseAsync());
    }

    [TestMethod]
    public async Task TheComShimServesTheToolsOverStdio()
    {
        using var test = new TestWorkspace();
        var workspace = Path.GetDirectoryName(WriteWorkspace(test))!;
        var settings = new Settings();
        new TrustStore(settings, test.Paths.SettingsFile).Trust(workspace);
        settings.Update(test.Paths.SettingsFile, s => (s.Mcp ??= new McpSettings()).AllowIds = ["pass", "fail", "long"]);
        await using var client = await McpClient.StartInitializedAsync(test, test.Root);

        var tools = (await client.CallAsync("tools/list"))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order();
        CollectionAssert.AreEqual(new[] { "compare_benchmarks", "get_log", "get_stats", "list_scripts", "run_script" }, tools.ToArray());

        var listed = JsonNode.Parse(TextOf(await client.CallToolAsync("list_scripts", new JsonObject { ["directory"] = workspace })))!;
        CollectionAssert.AreEqual(new[] { "pass", "fail", "long" },
            listed["scripts"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToArray());

        var passed = await client.RunScriptAsync(workspace, "pass");
        Assert.AreEqual(0, passed["exitCode"]!.GetValue<int>());
        Assert.IsTrue(passed["succeeded"]!.GetValue<bool>());
        Assert.AreEqual("exited", passed["outcome"]!.GetValue<string>());
        var runId = passed["runId"]!.GetValue<string>();
        Assert.AreEqual(RunTriggers.Agent("test-agent"), HistoryStore.For(test.Paths, "mcp-fixture").Recent().Single(r => r.Id == runId).Trigger);

        var failed = await client.RunScriptAsync(workspace, "fail");
        Assert.AreEqual(1, failed["exitCode"]!.GetValue<int>());
        var error = failed["errors"]!.AsArray().Single(e => e!["file"] is not null)!;
        Assert.AreEqual(3, error["line"]!.GetValue<int>());
        Assert.IsTrue(File.Exists(failed["logPath"]!.GetValue<string>()));

        var log = await client.CallToolAsync("get_log", new JsonObject { ["directory"] = workspace, ["runId"] = runId, ["tail"] = 2 });
        Assert.AreEqual("two" + Environment.NewLine + "three", TextOf(log));
        var range = await client.CallToolAsync("get_log",
            new JsonObject { ["directory"] = workspace, ["runId"] = runId, ["fromLine"] = 1, ["toLine"] = 1 });
        Assert.AreEqual("one", TextOf(range));

        var withLog = await client.CallToolAsync("run_script", new JsonObject { ["directory"] = workspace, ["id"] = "long", ["errorsOnly"] = false });
        var lines = withLog["content"]![1]!["text"]!.GetValue<string>().Split(Environment.NewLine);
        Assert.HasCount(McpTools.MaxLogLines + 1, lines);
        StringAssert.Contains(lines[0], "100 earlier lines left out");
        Assert.AreEqual("101", lines[1]);
        Assert.AreEqual("2100", lines[^1]);

        var refused = await client.CallToolAsync("run_script", new JsonObject { ["directory"] = workspace, ["id"] = "blocked" });
        Assert.IsTrue(refused["isError"]!.GetValue<bool>());
        StringAssert.Contains(TextOf(refused), "allowIds");

        Assert.AreEqual(0, await client.CloseAsync());
    }

    [TestMethod]
    public async Task ClosingTheServerStopsARunInFlightAndRecordsItAsStopped()
    {
        using var test = new TestWorkspace();
        var workspace = Path.GetDirectoryName(WriteWorkspace(test))!;
        new TrustStore(new Settings(), test.Paths.SettingsFile).Trust(workspace);
        await using var client = await McpClient.StartInitializedAsync(test, test.Root);

        await client.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 99,
            ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "run_script", ["arguments"] = new JsonObject { ["directory"] = workspace, ["id"] = "sleep" } },
        });
        await Eventually(() => File.Exists(Path.Combine(workspace, "started.txt")));

        Assert.AreEqual(0, await client.CloseAsync(expectNoOutput: false));
        var record = HistoryStore.For(test.Paths, "mcp-fixture").Recent().Single();
        Assert.AreEqual(RunOutcome.Stopped, record.Outcome);
    }

    [TestMethod]
    public async Task TheServerIsOffUntilTheUserTurnsItOn()
    {
        using var test = new TestWorkspace();
        var workspace = Path.GetDirectoryName(WriteWorkspace(test))!;
        new TrustStore(new Settings(), test.Paths.SettingsFile).Trust(workspace);
        var error = new StringWriter();

        var exitCode = await new CliRunner(test.Paths, new Settings(), new StringWriter(), error).RunAsync(["mcp"], workspace);

        Assert.AreEqual(CliRunner.Failure, exitCode);
        StringAssert.Contains(error.ToString(), McpSettings.TurnedOff);
        await using var tools = new McpTools(test.Paths);
        var refused = await tools.ListScripts(workspace);
        Assert.IsTrue(refused.IsError);
        StringAssert.Contains(((TextContentBlock)refused.Content.Single()).Text, McpSettings.TurnedOff);
    }

    [TestMethod]
    public async Task CompareBenchmarksReturnsTheSameJsonAsTheCommandLine()
    {
        using var test = new TestWorkspace();
        var directory = Path.GetDirectoryName(WriteWorkspace(test))!;
        var settings = new Settings();
        new TrustStore(settings, test.Paths.SettingsFile).Trust(directory);
        var store = HistoryStore.For(test.Paths, "mcp-fixture");
        CliTests.AddBenchmarkRun(store, 0, parseNs: 1000);
        var current = CliTests.AddBenchmarkRun(store, 1, parseNs: 1300);
        await using var tools = EnabledTools(test);
        var output = new StringWriter();

        var result = await tools.CompareBenchmarks(directory, current.Id);
        var exitCode = await new CliRunner(test.Paths, settings, output, new StringWriter()).RunAsync(["compare", current.Id, "--json", "-w", directory], test.Root);

        Assert.AreEqual(CliCompare.Regression, exitCode);
        Assert.AreNotEqual(true, result.IsError);
        var text = ((TextContentBlock)result.Content.Single()).Text;
        Assert.AreEqual(output.ToString().TrimEnd(), text);
        Assert.IsTrue(JsonNode.Parse(text)!["regressed"]!.GetValue<bool>());
    }

    private static void EnableMcp(TestWorkspace test) =>
        new Settings().Update(test.Paths.SettingsFile, s => (s.Mcp ??= new McpSettings()).Enabled = true);

    private static McpTools EnabledTools(TestWorkspace test)
    {
        EnableMcp(test);
        return new McpTools(test.Paths);
    }

    private static string TextOf(JsonNode result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static string WriteWorkspace(TestWorkspace test)
    {
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "workspace")).FullName;
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "src", "thing.cs"), "");
        File.WriteAllText(Path.Combine(directory, "pass.bat"), "@echo off\r\necho one\r\necho two\r\necho three\r\n");
        File.WriteAllText(Path.Combine(directory, "fail.bat"), "@echo off\r\necho src\\thing.cs(3,5): error CS1002: ; expected\r\nexit /b 1\r\n");
        File.WriteAllText(Path.Combine(directory, "long.bat"), "@echo off\r\nfor /L %%i in (1,1,2100) do echo %%i\r\n");
        File.WriteAllText(Path.Combine(directory, "sleep.bat"), "@echo off\r\necho started> \"%~dp0started.txt\"\r\nping -n 60 127.0.0.1 >nul\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            {
              "id": "mcp-fixture",
              "name": "MCP fixture",
              "scripts": [
                { "id": "pass", "name": "Pass", "path": "pass.bat" },
                { "id": "fail", "name": "Fail", "path": "fail.bat", "errorPatterns": [ "error CS\\d+" ] },
                { "id": "blocked", "name": "Blocked", "path": "pass.bat" },
                { "id": "long", "name": "Long", "path": "long.bat" },
                { "id": "sleep", "name": "Sleep", "path": "sleep.bat" }
              ]
            }
            """);
        return Path.Combine(directory, "batchpad.json");
    }

    /// <summary>Newline-delimited JSON-RPC over the real <c>batchpad.com</c>, failing on anything else on stdout.</summary>
    private sealed class McpClient : IAsyncDisposable
    {
        private readonly Process _process;
        private int _nextId;

        private McpClient(Process process) => _process = process;

        public static async Task<McpClient> StartInitializedAsync(TestWorkspace test, string workingDirectory)
        {
            var client = Start(test, workingDirectory);
            var initialized = await client.CallAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "test-agent", ["version"] = "1" },
            });
            Assert.AreEqual("batchpad", initialized["serverInfo"]!["name"]!.GetValue<string>());
            await client.NotifyAsync("notifications/initialized");
            return client;
        }

        private static McpClient Start(TestWorkspace test, string workingDirectory)
        {
            EnableMcp(test);
            var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "batchpad.com"))
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
            };
            startInfo.Environment[App.DataDirectoryVariable] = test.Paths.DataDirectory;
            startInfo.ArgumentList.Add("mcp");
            var process = Process.Start(startInfo)!;
            process.StandardInput.AutoFlush = true;
            process.ErrorDataReceived += (_, _) => { };
            process.BeginErrorReadLine();
            return new McpClient(process);
        }

        public async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null)
        {
            var message = await RequestAsync(method, parameters);
            Assert.IsNull(message["error"], message.ToJsonString());
            return message["result"]!;
        }

        public async Task<JsonNode> RequestAsync(string method, JsonObject? parameters = null)
        {
            var id = ++_nextId;
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(Limit)
                    ?? throw new AssertFailedException($"stdout closed while waiting for {method}.");
                var message = JsonNode.Parse(line)!;
                Assert.AreEqual("2.0", message["jsonrpc"]?.GetValue<string>(), line);
                if (message["id"]?.GetValue<int>() == id)
                    return message;
            }
        }

        public Task NotifyAsync(string method) => SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method });

        public Task<JsonNode> CallToolAsync(string name, JsonObject arguments) =>
            CallAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments });

        public async Task<JsonNode> RunScriptAsync(string directory, string id)
        {
            var result = await CallToolAsync("run_script", new JsonObject { ["directory"] = directory, ["id"] = id });
            Assert.IsNull(result["isError"], result.ToJsonString());
            return JsonNode.Parse(TextOf(result))!;
        }

        public async Task<int> CloseAsync(bool expectNoOutput = true)
        {
            _process.StandardInput.Close();
            var rest = await _process.StandardOutput.ReadToEndAsync().WaitAsync(Limit);
            await _process.WaitForExitAsync().WaitAsync(Limit);
            if (expectNoOutput)
                Assert.AreEqual("", rest.Trim());
            return _process.ExitCode;
        }

        public Task SendAsync(JsonObject message) => _process.StandardInput.WriteLineAsync(message.ToJsonString());

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
