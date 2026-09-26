using System.Text.Json.Nodes;
using BatchPad.App.Cli;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class CliTelemetryTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "cli");

    [TestMethod]
    public async Task ACliRunWritesAnEventBeforeItExits()
    {
        using var test = new TestWorkspace();
        var jsonl = Path.Combine(test.Root, "runs.jsonl");
        var settings = new Settings { Telemetry = new TelemetryOptions { Sinks = [new SinkConfig { Type = SinkTypes.Jsonl, Path = jsonl }] } };
        var launcher = new FakeLauncher();
        var runner = Runner(test, settings, launcher, Fixture);

        var running = runner.RunAsync(["run", "exit-three", "-w", Fixture], test.Root);
        launcher.Started.Single().Finish(RunOutcome.Exited, 3);

        Assert.AreEqual(3, await running);
        var line = JsonNode.Parse(File.ReadAllLines(jsonl).Single())!;
        Assert.AreEqual("cli", line["trigger"]!.GetValue<string>());
        Assert.AreEqual("exit-three", line["script"]!["id"]!.GetValue<string>());
        Assert.AreEqual("cli-fixture", line["workspace"]!["id"]!.GetValue<string>());
        Assert.AreEqual("CLI fixture", line["workspace"]!["name"]!.GetValue<string>());
        Assert.AreEqual(3, line["exitCode"]!.GetValue<int>());
    }

    [TestMethod]
    public async Task ATelemetryBlockInTheWorkspaceFileSendsNothing()
    {
        using var test = new TestWorkspace();
        var workspace = Directory.CreateDirectory(Path.Combine(test.Root, "repo")).FullName;
        var jsonl = Path.Combine(test.Root, "runs.jsonl");
        File.WriteAllText(Path.Combine(workspace, "hello.bat"), "@echo hi\r\n");
        File.WriteAllText(Path.Combine(workspace, "batchpad.json"), $$"""
            {
              "id": "cloned",
              "scripts": [ { "id": "hello", "path": "hello.bat" } ],
              "telemetry": { "sinks": [ { "type": "jsonl", "path": {{System.Text.Json.JsonSerializer.Serialize(jsonl)}} } ] }
            }
            """);
        var launcher = new FakeLauncher();
        var runner = Runner(test, new Settings(), launcher, workspace);

        var running = runner.RunAsync(["run", "hello", "-w", workspace], test.Root);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);

        Assert.AreEqual(0, await running);
        Assert.IsFalse(File.Exists(jsonl));
        Assert.IsFalse(Directory.Exists(Path.Combine(test.Paths.LocalDirectory, "telemetry")));
    }

    private static CliRunner Runner(TestWorkspace test, Settings settings, FakeLauncher launcher, string trusted)
    {
        new TrustStore(settings, test.Paths.SettingsFile).Trust(trusted);
        return new CliRunner(test.Paths, settings, new StringWriter(), new StringWriter(), launcher) { EnvironmentVariable = _ => null };
    }
}
