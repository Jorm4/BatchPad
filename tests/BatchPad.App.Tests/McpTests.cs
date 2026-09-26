using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.App.Cli;
using BatchPad.Core.History;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;
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
        var tools = new McpTools(test.Paths);

        foreach (var result in new[] { await tools.ListScripts(directory), await tools.GetStats(Path.Combine(directory, "src")) })
        {
            Assert.IsTrue(result.IsError);
            StringAssert.Contains(((TextContentBlock)result.Content.Single()).Text, "until you trust this workspace folder");
        }
        Assert.IsTrue((await tools.GetLog("any")).IsError);
        Assert.IsTrue((await tools.GetLog(@"..\..\settings")).IsError);
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
        settings.Update(test.Paths.SettingsFile, s => s.Mcp = new McpSettings { AllowIds = ["pass", "fail"] });
        await using var client = await McpClient.StartInitializedAsync(test, test.Root);

        var tools = (await client.CallAsync("tools/list"))["tools"]!.AsArray().Select(t => t!["name"]!.GetValue<string>()).Order();
        CollectionAssert.AreEqual(new[] { "get_log", "get_stats", "list_scripts", "run_script" }, tools.ToArray());

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

        var log = await client.CallToolAsync("get_log", new JsonObject { ["runId"] = runId, ["tail"] = 2 });
        Assert.AreEqual("two" + Environment.NewLine + "three", TextOf(log));
        var range = await client.CallToolAsync("get_log", new JsonObject { ["runId"] = runId, ["fromLine"] = 1, ["toLine"] = 1 });
        Assert.AreEqual("one", TextOf(range));

        var refused = await client.CallToolAsync("run_script", new JsonObject { ["directory"] = workspace, ["id"] = "blocked" });
        Assert.IsTrue(refused["isError"]!.GetValue<bool>());
        StringAssert.Contains(TextOf(refused), "allowIds");

        Assert.AreEqual(0, await client.CloseAsync());
    }

    private static string TextOf(JsonNode result) => result["content"]![0]!["text"]!.GetValue<string>();

    private static string WriteWorkspace(TestWorkspace test)
    {
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "workspace")).FullName;
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        File.WriteAllText(Path.Combine(directory, "src", "thing.cs"), "");
        File.WriteAllText(Path.Combine(directory, "pass.bat"), "@echo off\r\necho one\r\necho two\r\necho three\r\n");
        File.WriteAllText(Path.Combine(directory, "fail.bat"), "@echo off\r\necho src\\thing.cs(3,5): error CS1002: ; expected\r\nexit /b 1\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            {
              "id": "mcp-fixture",
              "name": "MCP fixture",
              "scripts": [
                { "id": "pass", "name": "Pass", "path": "pass.bat" },
                { "id": "fail", "name": "Fail", "path": "fail.bat", "errorPatterns": [ "error CS\\d+" ] },
                { "id": "blocked", "name": "Blocked", "path": "pass.bat" }
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

        public async Task<int> CloseAsync()
        {
            _process.StandardInput.Close();
            var rest = await _process.StandardOutput.ReadToEndAsync().WaitAsync(Limit);
            await _process.WaitForExitAsync().WaitAsync(Limit);
            Assert.AreEqual("", rest.Trim());
            return _process.ExitCode;
        }

        private Task SendAsync(JsonObject message) => _process.StandardInput.WriteLineAsync(message.ToJsonString());

        public ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
