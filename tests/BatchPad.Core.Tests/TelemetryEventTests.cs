using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TelemetryEventTests
{
    internal static readonly TelemetryWorkspace Workspace = new("5c0f", "My Project");

    internal static RunRecord SampleRecord() => new()
    {
        Id = "20260927-091203-412-3f9a1c2",
        ParentRunId = "20260927-091200-000-0a1b2c3",
        StepId = "build",
        NodeKey = "Workspace:id:build",
        Tree = TreeKind.Workspace,
        NodeId = "build",
        Path = "scripts/build.bat",
        Name = "Build",
        Command = "scripts\\build.bat --release --token hunter2",
        ExtraArguments = "--verbose",
        Values = new() { ["config"] = "--release", ["token"] = RunRecord.Masked },
        Trigger = "agent:example",
        StartedAt = new DateTimeOffset(2026, 9, 27, 11, 12, 3, 412, TimeSpan.FromHours(2)),
        Duration = TimeSpan.FromMilliseconds(94210),
        QueuedMs = 850,
        Outcome = RunOutcome.Exited,
        Folder = "Build",
        Tags = ["native"],
        Git = new GitInfo("main", "3f9a1c2"),
        Tests = new TestSummary(1284, 2, 3, ["a.b"], [new TestTiming("a.c", 2.5)]),
        Errors = [new ErrorLine("error: boom", "src/a.c", 3)],
        Machine = "WS-042",
        User = "alex",
        BatchPadVersion = "0.2.0",
    };

    [TestMethod]
    public void AnEventFromASampleRecordHasTheDocumentedShape()
    {
        var record = SampleRecord();

        var json = TelemetryEvents.From(record, Workspace, new TelemetryOptions()).ToJson();

        var expected = File.ReadAllText(Fixtures.Path("telemetry", "event.json")).Replace("EVENT-ID", TelemetryEvents.EventIdOf(record.Id));
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(json)), json);
    }

    [TestMethod]
    public void AnEventNamesItsCheckout()
    {
        var record = SampleRecord() with { Checkout = new Checkout("wt", CheckoutKind.Worktree, "wt", "feature", "3f9a1c2", "repo") };

        var checkout = JsonNode.Parse(TelemetryEvents.From(record, Workspace, new TelemetryOptions()).ToJson())!["checkout"]!;

        Assert.AreEqual("wt", checkout["name"]!.GetValue<string>());
        Assert.AreEqual("worktree", checkout["kind"]!.GetValue<string>());
    }

    [TestMethod]
    public void ValuesAreSentOnlyWhenIncludedAndSecretsNever()
    {
        var record = SampleRecord();

        var without = TelemetryEvents.From(record, Workspace, new TelemetryOptions()).ToJson();
        var with = TelemetryEvents.From(record, Workspace, new TelemetryOptions { IncludeValues = true, User = true }).ToJson();

        Assert.DoesNotContain("values", without);
        Assert.DoesNotContain("alex", without);
        var values = JsonNode.Parse(with)!["values"]!.AsObject();
        Assert.AreEqual("--release", values["config"]!.GetValue<string>());
        Assert.IsFalse(values.ContainsKey("token"));
        Assert.AreEqual("alex", JsonNode.Parse(with)!["user"]!.GetValue<string>());
        foreach (var json in new[] { without, with })
        {
            Assert.DoesNotContain("hunter2", json);
            Assert.DoesNotContain(RunRecord.Masked, json);
            Assert.DoesNotContain("build.bat", json);
            Assert.DoesNotContain("--verbose", json);
            Assert.DoesNotContain("boom", json);
        }
    }

    [TestMethod]
    public void MachineCanBeLeftOut()
    {
        var json = TelemetryEvents.From(SampleRecord(), Workspace, new TelemetryOptions { Machine = false }).ToJson();

        Assert.DoesNotContain("WS-042", json);
    }

    [TestMethod]
    public void HashNamesHashesScriptFolderAndWorkspaceNamesConsistently()
    {
        var options = new TelemetryOptions { HashNames = true };

        var first = TelemetryEvents.From(SampleRecord(), Workspace, options, "salt-1");
        var again = TelemetryEvents.From(SampleRecord() with { Id = "other" }, Workspace, options, "salt-1");
        var otherInstall = TelemetryEvents.From(SampleRecord(), Workspace, options, "salt-2");

        Assert.AreEqual(TelemetryEvents.Hash("Build", "salt-1"), first.Script.Name);
        Assert.AreEqual((first.Script.Id, first.Script.Name, first.Script.Folder), (again.Script.Id, again.Script.Name, again.Script.Folder));
        Assert.AreEqual(first.Workspace.Name, again.Workspace.Name);
        Assert.AreNotEqual(first.Script.Name, otherInstall.Script.Name);
        var json = first.ToJson();
        Assert.DoesNotContain("My Project", json);
        Assert.DoesNotContain("\"Build\"", json);
        Assert.AreEqual("5c0f", first.Workspace.Id);
    }

    [TestMethod]
    public void AWorkflowRecordIsAWorkflowEvent()
    {
        var record = SampleRecord() with { Path = null, Command = "", ParentRunId = null, StepId = null };

        var telemetryEvent = TelemetryEvents.From(record, Workspace, new TelemetryOptions());

        Assert.AreEqual(TelemetryScript.WorkflowKind, telemetryEvent.Script.Kind);
        Assert.IsNull(telemetryEvent.ParentRunId);
    }

    [TestMethod]
    public void ATelemetryBlockInAWorkspaceFileIsNotASettingsSource()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), """
            { "scripts": [], "telemetry": { "sinks": [ { "type": "jsonl", "path": "runs.jsonl" } ] } }
            """);
        var paths = new AppPaths(dir.Path("data"));

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), paths);

        Assert.IsEmpty(loaded.Errors);
        Assert.IsNull(Settings.Load(paths.SettingsFile).Telemetry);
        Assert.IsFalse(File.Exists(paths.SettingsFile));
    }

    [TestMethod]
    public void AnEnvReferenceExpandsAtSendTimeAndIsNotStored()
    {
        using var dir = new TempDir();
        var file = dir.Path("settings.json");
        var settings = new Settings();
        settings.Update(file, s => s.Telemetry = new TelemetryOptions
        {
            Sinks = [new SinkConfig { Type = SinkTypes.Http, Url = "https://collector.example.com/runs", Headers = new() { ["Authorization"] = "Bearer ${env:BP_TELEMETRY_TEST_TOKEN}" } }],
        });
        Environment.SetEnvironmentVariable("BP_TELEMETRY_TEST_TOKEN", "s3cr3t-4711");

        var sink = Settings.Load(file).Telemetry!.Sinks.Single();
        var resolved = sink.Resolve();

        Assert.AreEqual("Bearer s3cr3t-4711", resolved.Headers!["Authorization"]);
        Assert.AreEqual("Bearer ${env:BP_TELEMETRY_TEST_TOKEN}", sink.Headers!["Authorization"]);
        Assert.DoesNotContain("s3cr3t-4711", File.ReadAllText(file));
        StringAssert.Contains(File.ReadAllText(file), "${env:BP_TELEMETRY_TEST_TOKEN}");
    }

    [TestMethod]
    public void TheSinkKeyIgnoresCredentials()
    {
        var sink = new SinkConfig { Type = SinkTypes.Elastic, Url = "https://es.example.com:9200", Index = "runs", ApiKey = "${env:A}" };
        var other = new SinkConfig { Type = SinkTypes.Elastic, Url = "https://es.example.com:9200", Index = "runs", ApiKey = "${env:B}" };

        Assert.AreEqual(sink.Key, other.Key);
        Assert.AreNotEqual(sink.Key, new SinkConfig { Type = SinkTypes.Elastic, Url = "https://es.example.com:9200", Index = "other" }.Key);
    }
}
