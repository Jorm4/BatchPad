using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.App.Cli;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

/// <summary>An agent's run seen end to end; the MCP round trip is covered by <see cref="McpTests"/>.</summary>
[TestClass]
public sealed class TelemetryAcceptanceTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "cli");

    [TestMethod]
    public async Task AnAgentsCliRunReachesHistoryInsightsAndTheJsonlSink()
    {
        using var test = new TestWorkspace();
        var jsonl = Path.Combine(test.Root, "runs.jsonl");
        var settings = new Settings { Telemetry = new TelemetryOptions { Sinks = [new SinkConfig { Type = SinkTypes.Jsonl, Path = jsonl }] } };
        new TrustStore(settings, test.Paths.SettingsFile).Trust(Fixture);
        var output = new StringWriter();
        var launcher = new FakeLauncher();
        var runner = new CliRunner(test.Paths, settings, output, new StringWriter(), launcher)
        {
            EnvironmentVariable = name => name == "CLAUDECODE" ? "1" : null,
        };
        var observer = HistoryStore.For(test.Paths, "cli-fixture");
        var seen = new ConcurrentQueue<RunRecord>();
        observer.RunRecorded += seen.Enqueue;
        Assert.IsEmpty(observer.Recent());

        var running = runner.RunAsync(["run", "exit-three", "--json", "-w", Fixture], test.Root);
        launcher.Started.Single().Finish(RunOutcome.Exited, 3);

        Assert.AreEqual(3, await running);
        var runId = JsonDocument.Parse(output.ToString()).RootElement.GetProperty("runId").GetString();
        await Eventually(() => seen.Any(r => r.Id == runId), () => "The other history store never saw the CLI run.");
        observer.RunRecorded -= seen.Enqueue;
        Assert.AreEqual("agent:claude-code", seen.Single(r => r.Id == runId).Trigger);

        var stats = RunStats.Compute(observer.Recent(), TimeSpan.FromDays(1), DateTimeOffset.Now);
        Assert.AreEqual(1, stats.Runs);
        Assert.AreEqual(1, stats.Triggers.Single(t => t.Name == TriggerClasses.Agents).Runs);

        await Eventually(() => CompleteLines(jsonl).Length == 1, () => "The jsonl sink got no event.");
        var sent = JsonNode.Parse(CompleteLines(jsonl).Single())!;
        Assert.AreEqual(runId, sent["runId"]!.GetValue<string>());
        Assert.AreEqual("agent:claude-code", sent["trigger"]!.GetValue<string>());
        Assert.AreEqual(3, sent["exitCode"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task TwoLockManagersOnOneFolderExcludeEachOther()
    {
        using var temp = new TempDir();
        var app = new LockManager(temp.Root);
        var cli = new LockManager(temp.Root);

        var held = await app.AcquireAsync(["native-build"], new object(), holder: "Build");

        Assert.ThrowsExactly<LockBusyException>(() => cli.AcquireAsync(["native-build"], new object(), wait: false).GetAwaiter().GetResult());
        StringAssert.Contains(cli.DescribeHolder("native-build"), "Build");
        held.Dispose();
        (await cli.AcquireAsync(["native-build"], new object()).WaitAsync(Limit)).Dispose();
    }

    private static string[] CompleteLines(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var text = new StreamReader(stream).ReadToEnd();
            var end = text.LastIndexOf('\n');
            return end < 0 ? [] : text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }
}
