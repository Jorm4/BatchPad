using System.Text.Json;
using BatchPad.App.Cli;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

/// <summary>Drives the demo's benchmarks, command-line trust, saved secrets and scheduled runs end to end.</summary>
[TestClass]
public sealed class Phase11AcceptanceTests
{
    [TestMethod]
    public void TheDemoBenchmarksRunTwiceThenCompare()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();

        var (refused, refusal) = CliTests.RunShim(test, "trust", "--workspace", demo);
        Assert.AreEqual(CliRunner.Failure, refused, refusal);
        StringAssert.Contains(refusal, "input is redirected");
        TrustStore.Load(test.Paths).Trust(demo);

        var first = RunBench(test, demo);
        var second = RunBench(test, demo);
        var (code, output) = CliTests.RunShim(test, "compare", second, "--workspace", demo);

        Assert.IsTrue(code is 0 or CliCompare.Regression, output);
        StringAssert.Contains(output, $"real time vs {first}");
        StringAssert.Contains(output, "Benchmark");
        foreach (var name in new[] { "BM_Parse", "BM_Sort/1024", "BM_Sort/65536", "BM_Hash" })
            StringAssert.Contains(output, name);
    }

    [TestMethod]
    public async Task ASecretSetAtTheKeyboardReachesAScheduledRun()
    {
        using var test = new TestWorkspace();
        var workspace = ScheduledWorkspace(test);
        TrustStore.Load(test.Paths).Trust(workspace);
        var secrets = new FakeSecretStore();
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliRunner(test.Paths, Settings.Load(test.Paths.SettingsFile), output, error)
        {
            EnvironmentVariable = _ => null,
            Prompt = new FakePrompt("s3cret"),
            Secrets = secrets,
            TaskRegistrar = new FakeTaskRegistrar(),
        };

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["run", "--schedule", "nightly", "-w", workspace], test.Root));
        StringAssert.Contains(error.ToString(), "needs a value for token");

        Assert.AreEqual(0, await runner.RunAsync(["secret", "set", "token", "-w", workspace], test.Root), error.ToString());
        Assert.AreEqual(0, await runner.RunAsync(["run", "--schedule", "nightly", "-w", workspace], test.Root), error.ToString());

        var record = HistoryStore.For(test.Paths, "phase11").Recent().Single();
        Assert.AreEqual(RunTriggers.Schedule("phase11:nightly"), record.Trigger);
        Assert.AreEqual(RunRecord.Masked, record.Values["token"]!.GetValue<string>());
        Assert.DoesNotContain("s3cret", output.ToString());
    }

    private static string RunBench(TestWorkspace test, string demo)
    {
        var (code, output) = CliTests.RunShim(test, "run", "bench", "--workspace", demo, "--json");
        Assert.AreEqual(0, code, output);
        using var result = JsonDocument.Parse(output);
        return result.RootElement.GetProperty("runId").GetString()!;
    }

    /// <summary>A workspace with a confirmed <c>nightly</c> schedule whose script fails unless it gets the <c>token</c> secret.</summary>
    private static string ScheduledWorkspace(TestWorkspace test)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(test.Root, "scheduled")).FullName;
        File.WriteAllText(Path.Combine(workspace, "deploy.bat"), "@if \"%~1\"==\"s3cret\" (echo deployed) else (exit /b 5)\r\n");
        File.WriteAllText(Path.Combine(workspace, WorkspaceLocator.FileName), """
            { "id": "phase11", "scripts": [
              { "id": "deploy", "path": "deploy.bat", "params": [ { "name": "token", "type": "secret" } ] } ] }
            """);
        var userFile = test.Paths.UserFile("phase11");
        Directory.CreateDirectory(Path.GetDirectoryName(userFile)!);
        const string schedule = """{ "id": "nightly", "target": "workspace:deploy", "trigger": { "cron": "0 2 * * *" } """;
        File.WriteAllText(userFile, $$"""{ "schedules": [ {{schedule}} } ] }""");
        var hash = DefinitionHash.Of(ScheduleEntry.For(WorkspaceLoader.Load(Path.Combine(workspace, WorkspaceLocator.FileName), test.Paths))
            .Single().Target!);
        File.WriteAllText(userFile, $$"""{ "schedules": [ {{schedule}}, "definitionHash": "{{hash}}" } ] }""");
        return workspace;
    }
}
