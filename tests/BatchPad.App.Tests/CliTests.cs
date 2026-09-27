using System.Diagnostics;
using System.Text;
using System.Text.Json;
using BatchPad.App.Cli;
using BatchPad.Core.History;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class CliTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "cli");

    [TestMethod]
    public void TheParserReadsSetsAndFlags()
    {
        var command = CliCommand.Parse(["run", "build", "--set", "config=Release", "--set", "filter=a=b", "--yes", "-w", "repo"]);

        Assert.AreEqual(CliVerb.Run, command.Verb);
        Assert.AreEqual("build", command.Target);
        Assert.AreEqual("repo", command.Workspace);
        Assert.IsTrue(command.Yes);
        Assert.AreEqual("Release", command.Values["config"]);
        Assert.AreEqual("a=b", command.Values["filter"]);
        Assert.IsFalse(CliCommand.IsCli(["samples/demo"]));

        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["run", "build", "--set", "config"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["run"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["run", "build", "--force"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["list", "--yes"]));
    }

    [TestMethod]
    public void TheParserAcceptsOnlyTheVerbsThatSelectTheCli()
    {
        foreach (var verb in new[] { "RUN", "List", "0", "1" })
        {
            Assert.IsFalse(CliCommand.IsCli([verb, "x"]));
            Assert.Throws<CliUsageException>(() => CliCommand.Parse([verb, "x"]));
        }
    }

    [TestMethod]
    public async Task AnAmbiguousChoiceIsAFailureNotACrash()
    {
        using var test = new TestWorkspace();
        var settings = new Settings();
        new TrustStore(settings, test.Paths.SettingsFile).Trust(Fixture);
        var error = new StringWriter();
        var runner = new CliRunner(test.Paths, settings, new StringWriter(), error);

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["run", "exit-three", "--set", "speed=Fast", "-w", Fixture], test.Root));
        StringAssert.Contains(error.ToString(), "label of 2 choices");
    }

    [TestMethod]
    public async Task ARunRelaysOutputPassesTheExitCodeAndIsRecorded()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (runner, output, error) = Runner(test, launcher);

        var running = runner.RunAsync(["run", "exit-three", "--set", "who=me", "--workspace", Fixture], test.Root);
        var request = launcher.Requests.Single();
        launcher.Started[0].Emit("out line", OutputStream.Stdout);
        launcher.Started[0].Emit("err line", OutputStream.Stderr);
        launcher.Started[0].Finish(RunOutcome.Exited, 3);

        Assert.AreEqual(3, await running);
        Assert.IsTrue(request.Unattended);
        Assert.AreEqual("me", request.Values!["who"]!.GetValue<string>());
        StringAssert.Contains(output.ToString(), "out line");
        StringAssert.Contains(error.ToString(), "err line");
        var record = HistoryStore.For(test.Paths, "cli-fixture").Recent().Single();
        Assert.AreEqual(RunTriggers.Cli, record.Trigger);
        Assert.AreEqual(3, record.ExitCode);
    }

    [TestMethod]
    public async Task AConfirmScriptNeedsYes()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (runner, _, error) = Runner(test, launcher);

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["run", "Deploy", "-w", Fixture], test.Root));
        StringAssert.Contains(error.ToString(), "needs confirmation");
        Assert.IsEmpty(launcher.Requests);

        var confirmed = runner.RunAsync(["run", "deploy", "--yes", "-w", Fixture], test.Root);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        Assert.AreEqual(0, await confirmed);
        Assert.IsTrue(launcher.Requests.Single().Confirmed);
    }

    [TestMethod]
    public async Task ListPrintsTheIdsAndAnUnknownTargetIsAUsageError()
    {
        using var test = new TestWorkspace();
        var (runner, output, _) = Runner(test, new FakeLauncher());

        Assert.AreEqual(0, await runner.RunAsync(["list", "-w", Fixture], test.Root));
        CollectionAssert.AreEqual(new[] { "exit-three\tExit three", "deploy\tDeploy", "flow\tFlow", "greeting\tGrüße ✓", "build\tBuild", "locked\tLocked" },
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual(CliRunner.UsageError, await runner.RunAsync(["run", "missing", "-w", Fixture], test.Root));
    }

    [TestMethod]
    public void UnreadableSettingsFallBackToDefaults()
    {
        using var test = new TestWorkspace();
        Directory.CreateDirectory(test.Paths.DataDirectory);
        using var locked = new FileStream(test.Paths.SettingsFile, FileMode.Create, FileAccess.Write, FileShare.None);
        var error = new StringWriter();

        Assert.IsNotNull(App.LoadSettings(test.Paths, error));
        Assert.IsFalse(string.IsNullOrEmpty(error.ToString()));
    }

    [TestMethod]
    public void TheComShimRelaysAWorkflowsOutputAndExitCode()
    {
        using var test = new TestWorkspace();
        new TrustStore(new Settings(), test.Paths.SettingsFile).Trust(Fixture);

        var (code, output) = RunShim(test, "run", "flow", "--workspace", Fixture);

        Assert.AreEqual(3, code, output);
        StringAssert.Contains(output, "> drei-✓");
    }

    [TestMethod]
    public void TheParserReadsTheAgentOptions()
    {
        var run = CliCommand.Parse(["run", "build", "--json", "--no-wait", "--agent", "x"]);
        Assert.IsTrue(run.Json && run.NoWait);
        Assert.AreEqual("x", run.Agent);
        Assert.IsTrue(CliCommand.Parse(["run", "build", "--errors-only"]).ErrorsOnly);

        var log = CliCommand.Parse(["log", "run-1", "--tail", "2", "--errors"]);
        Assert.AreEqual(CliVerb.Log, log.Verb);
        Assert.AreEqual("run-1", log.Target);
        Assert.AreEqual(2, log.Tail);
        Assert.IsTrue(log.ErrorsOnly);

        var stats = CliCommand.Parse(["stats", "--since", "30d", "--json"]);
        Assert.AreEqual(TimeSpan.FromDays(30), stats.Since);
        Assert.IsTrue(stats.Json);
        Assert.AreEqual(TimeSpan.FromDays(7), CliCommand.Parse(["stats"]).Since);
        Assert.IsTrue(CliCommand.IsCli(["log", "x"]) && CliCommand.IsCli(["stats"]));

        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["run", "build", "--json", "--errors-only"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["log"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["log", "x", "--tail", "0"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["stats", "--since", "2w"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["list", "--no-wait"]));
    }

    [TestMethod]
    public async Task ListJsonDescribesEachEntryAndItsParameters()
    {
        using var test = new TestWorkspace();
        var (runner, output, _) = Runner(test, new FakeLauncher());

        Assert.AreEqual(0, await runner.RunAsync(["list", "--json", "-w", Fixture], test.Root));

        var entries = JsonDocument.Parse(output.ToString()).RootElement.EnumerateArray().ToList();
        var exitThree = entries.Single(e => e.GetProperty("id").GetString() == "exit-three");
        Assert.AreEqual("script", exitThree.GetProperty("kind").GetString());
        var speed = exitThree.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "speed");
        Assert.AreEqual("choice", speed.GetProperty("type").GetString());
        CollectionAssert.AreEqual(new[] { "1:Fast", "2:Fast" },
            speed.GetProperty("choices").EnumerateArray().Select(c => $"{c.GetProperty("value").GetString()}:{c.GetProperty("label").GetString()}").ToArray());
        Assert.AreEqual("workflow", entries.Single(e => e.GetProperty("id").GetString() == "flow").GetProperty("kind").GetString());
    }

    [TestMethod]
    public async Task RunJsonPrintsOnlyTheResultWithErrorLocationsAndRecordsTheAgent()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (runner, output, _) = Runner(test, launcher);

        var running = runner.RunAsync(["run", "build", "--json", "--agent", "x", "-w", Fixture], test.Root);
        EmitBuild(launcher.Started.Single());

        Assert.AreEqual(1, await running);
        var result = JsonDocument.Parse(output.ToString()).RootElement;
        Assert.AreEqual("build", result.GetProperty("id").GetString());
        Assert.AreEqual(1, result.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("exited", result.GetProperty("outcome").GetString());
        var error = result.GetProperty("errors").EnumerateArray().Single(e => e.GetProperty("file").ValueKind == JsonValueKind.String);
        StringAssert.EndsWith(error.GetProperty("file").GetString(), Path.Combine("src", "thing.cs"));
        Assert.AreEqual(3, error.GetProperty("line").GetInt32());
        Assert.IsTrue(File.Exists(result.GetProperty("logPath").GetString()));
        var record = HistoryStore.For(test.Paths, "cli-fixture").Recent().Single();
        Assert.AreEqual(record.Id, result.GetProperty("runId").GetString());
        Assert.AreEqual(RunTriggers.Agent("x"), record.Trigger);
    }

    [TestMethod]
    public async Task ErrorsOnlyPrintsTheErrorsAndASummary()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (runner, output, error) = Runner(test, launcher);

        var running = runner.RunAsync(["run", "build", "--errors-only", "-w", Fixture], test.Root);
        EmitBuild(launcher.Started.Single());

        Assert.AreEqual(1, await running);
        var printed = output.ToString() + error;
        StringAssert.Contains(printed, "error CS1002");
        StringAssert.Contains(printed, "a warning on stderr");
        Assert.DoesNotContain("compiling", printed);
        StringAssert.Contains(output.ToString(), "Build: failed with exit code 1");
        StringAssert.Contains(output.ToString(), "log: ");
    }

    [TestMethod]
    public async Task ClaudeCodesEnvironmentMarksTheRunAsAnAgents()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (runner, _, _) = Runner(test, launcher, name => name == "CLAUDECODE" ? "1" : null);

        var running = runner.RunAsync(["run", "exit-three", "-w", Fixture], test.Root);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);

        Assert.AreEqual(0, await running);
        Assert.AreEqual("agent:claude-code", HistoryStore.For(test.Paths, "cli-fixture").Recent().Single().Trigger);
    }

    [TestMethod]
    public async Task LogPrintsARecordedRunsTailOrErrors()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var (runner, output, _) = Runner(test, launcher);
        var running = runner.RunAsync(["run", "build", "-w", Fixture], test.Root);
        EmitBuild(launcher.Started.Single());
        await running;
        var id = HistoryStore.For(test.Paths, "cli-fixture").Recent().Single().Id;
        output.GetStringBuilder().Clear();

        Assert.AreEqual(0, await runner.RunAsync(["log", id, "--tail", "2", "-w", Fixture], test.Root));
        CollectionAssert.AreEqual(new[] { BuildError, "a warning on stderr" }, Lines(output));

        output.GetStringBuilder().Clear();
        Assert.AreEqual(0, await runner.RunAsync(["log", id, "--errors", "-w", Fixture], test.Root));
        CollectionAssert.AreEqual(new[] { BuildError, "a warning on stderr" }, Lines(output));

        Assert.AreEqual(CliRunner.UsageError, await runner.RunAsync(["log", "missing", "-w", Fixture], test.Root));
    }

    [TestMethod]
    public async Task NoWaitFailsWhileTheLockIsHeld()
    {
        using var test = new TestWorkspace();
        var settings = new Settings();
        new TrustStore(settings, test.Paths.SettingsFile).Trust(Fixture);
        var error = new StringWriter();
        var runner = new CliRunner(test.Paths, settings, new StringWriter(), error) { EnvironmentVariable = _ => null };
        using var held = await LockManager.For(test.Paths).AcquireAsync(
            [LockKeys.For("cli-fixture-lock", null, Checkout.DirectoryOf(Fixture))], new object(), holder: "someone else");

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["run", "locked", "--no-wait", "-w", Fixture], test.Root));
        StringAssert.Contains(error.ToString(), "cli-fixture-lock");
        Assert.IsEmpty(HistoryStore.For(test.Paths, "cli-fixture").Recent());
        Assert.AreEqual(CliRunner.UsageError, await runner.RunAsync(["run", "flow", "--no-wait", "-w", Fixture], test.Root));
    }

    [TestMethod]
    public async Task StatsSummarisesTheHistoryAsTextOrJson()
    {
        using var test = new TestWorkspace();
        var (runner, output, _) = Runner(test, new FakeLauncher());
        var store = HistoryStore.For(test.Paths, "cli-fixture");
        var now = DateTimeOffset.Now;
        for (var i = 0; i < 4; i++)
        {
            var failed = i == 1;
            store.Add(new RunRecord
            {
                NodeKey = "workspace:build",
                Name = "Build",
                Trigger = RunTriggers.Agent("x"),
                StartedAt = now.AddMinutes(-20 + 5 * i),
                Duration = TimeSpan.FromSeconds(10 * (i + 1)),
                ExitCode = failed ? 1 : 0,
                Tests = new TestSummary(failed ? 1 : 2, failed ? 1 : 0, 0, failed ? ["Suite.Flaky"] : [], [new TestTiming("Suite.Slow", 12.5)]),
            }, []);
        }
        store.Add(new RunRecord { NodeKey = "workspace:build", Name = "Build", StartedAt = now.AddDays(-10), Duration = TimeSpan.FromSeconds(50) }, []);

        Assert.AreEqual(0, await runner.RunAsync(["stats", "-w", Fixture], test.Root));

        var text = output.ToString();
        StringAssert.Contains(text, "Last 7 days: 4 runs, 1m 40s in total");
        StringAssert.Matches(text, new System.Text.RegularExpressions.Regex(@"Build\s+4\s+1m 40s\s+25\.0s\s+40\.0s\s+\+133%\s+25%"));
        StringAssert.Contains(text, "(top level)  100%  (1m 40s)");
        StringAssert.Contains(text, "agents  100%  (1m 40s)");
        StringAssert.Contains(text, "Build  x4");
        StringAssert.Contains(text, "Suite.Slow  12.5s  (Build)");
        StringAssert.Contains(text, "Suite.Flaky  (Build: 1 failed, 2 passed later)");

        output.GetStringBuilder().Clear();
        Assert.AreEqual(0, await runner.RunAsync(["stats", "--since", "30d", "--json", "-w", Fixture], test.Root));
        var stats = JsonSerializer.Deserialize<RunStats>(output.ToString(), JsonSerializerOptions.Web)!;
        Assert.AreEqual(5, stats.Runs);
        Assert.AreEqual("Suite.Flaky", stats.FlakyTests.Single().Name);
    }

    [TestMethod]
    public void TheParserReadsCompareWithAnOptionalBaseline()
    {
        var command = CliCommand.Parse(["compare", "run-2", "run-1", "--cpu", "--json"]);

        Assert.AreEqual((CliVerb.Compare, "run-2", "run-1", true, true), (command.Verb, command.Target, command.Baseline, command.Cpu, command.Json));
        Assert.IsNull(CliCommand.Parse(["compare", "run-2"]).Baseline);
        Assert.AreEqual("'compare' needs a run id.", Assert.Throws<CliUsageException>(() => CliCommand.Parse(["compare"])).Message);
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["compare", "a", "b", "c"]));
    }

    [TestMethod]
    public async Task CompareExitsThreeWhenABenchmarkIsSlowerThanTheThreshold()
    {
        using var test = new TestWorkspace();
        var (runner, output, _) = Runner(test, new FakeLauncher());
        var store = HistoryStore.For(test.Paths, "cli-fixture");
        var baseline = AddBenchmarkRun(store, 0, parseNs: 1000);
        var steady = AddBenchmarkRun(store, 1, parseNs: 1030);
        var slower = AddBenchmarkRun(store, 2, parseNs: 1236);

        Assert.AreEqual(0, await runner.RunAsync(["compare", steady.Id, "-w", Fixture], test.Root));
        StringAssert.Contains(output.ToString(), $"vs {baseline.Id}");
        StringAssert.Matches(output.ToString(), new System.Text.RegularExpressions.Regex(@"BM_Parse\s+1\.00 us\s+1\.03 us\s+\+3\.0%\s+unchanged"));

        output.GetStringBuilder().Clear();
        Assert.AreEqual(CliCompare.Regression, await runner.RunAsync(["compare", slower.Id, "-w", Fixture], test.Root));
        StringAssert.Matches(output.ToString(), new System.Text.RegularExpressions.Regex(@"BM_Parse\s+1\.03 us\s+1\.24 us\s+\+20\.0%\s+slower"));

        output.GetStringBuilder().Clear();
        Assert.AreEqual(0, await runner.RunAsync(["compare", slower.Id, baseline.Id, "--cpu", "--json", "-w", Fixture], test.Root));
        var json = JsonDocument.Parse(output.ToString()).RootElement;
        Assert.AreEqual(baseline.Id, json.GetProperty("baselineRunId").GetString());
        Assert.IsFalse(json.GetProperty("regressed").GetBoolean());
        Assert.AreEqual("unchanged", json.GetProperty("benchmarks")[0].GetProperty("verdict").GetString());
    }

    [TestMethod]
    public async Task CompareFailsWhenTheNamedBaselineHasNoBenchmarks()
    {
        using var test = new TestWorkspace();
        var (runner, output, error) = Runner(test, new FakeLauncher());
        var store = HistoryStore.For(test.Paths, "cli-fixture");
        var plain = store.Add(new RunRecord { NodeKey = "Workspace:id:build", Name = "build", StartedAt = DateTimeOffset.Now.AddMinutes(-5) }, []);
        var current = AddBenchmarkRun(store, 1, parseNs: 1000);

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["compare", current.Id, plain.Id, "-w", Fixture], test.Root));
        StringAssert.Contains(error.ToString(), $"Run '{plain.Id}' recorded no benchmarks");
        Assert.AreEqual("", output.ToString());
    }

    internal static RunRecord AddBenchmarkRun(HistoryStore store, int minutes, double parseNs) =>
        store.Add(new RunRecord
        {
            NodeKey = "Workspace:id:bench",
            Name = "Bench",
            StartedAt = DateTimeOffset.Now.AddMinutes(minutes - 10),
            Benchmarks = new BenchmarkSummary([new("BM_Parse", parseNs, 500, 1000)], 5),
        }, []);

    [TestMethod]
    public void TheComShimPrintsAJsonResultForClaudeCode()
    {
        using var test = new TestWorkspace();
        new TrustStore(new Settings(), test.Paths.SettingsFile).Trust(Fixture);

        var (code, output) = RunShim(test, new Dictionary<string, string?> { ["CLAUDECODE"] = "1" }, "run", "build", "--json", "--workspace", Fixture);

        Assert.AreEqual(1, code, output);
        var result = JsonDocument.Parse(output.Split(Environment.NewLine)[0]).RootElement;
        Assert.AreEqual(1, result.GetProperty("exitCode").GetInt32());
        Assert.IsTrue(result.GetProperty("errors").EnumerateArray().Any(e => e.GetProperty("line").GetInt32() == 3));
        Assert.IsTrue(File.Exists(result.GetProperty("logPath").GetString()));
        Assert.AreEqual("agent:claude-code", HistoryStore.For(test.Paths, "cli-fixture").Recent().Single().Trigger);
    }

    private const string BuildError = @"src\thing.cs(3,5): error CS1002: ; expected";

    private static void EmitBuild(FakeProcess process)
    {
        process.Emit("compiling one", OutputStream.Stdout);
        process.Emit("compiling two", OutputStream.Stdout);
        process.Emit(BuildError, OutputStream.Stdout);
        process.Emit("a warning on stderr", OutputStream.Stderr);
        process.Finish(RunOutcome.Exited, 1);
    }

    private static string[] Lines(StringWriter writer) => writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    internal static (int ExitCode, string Output) RunShim(TestWorkspace test, params string[] args) => RunShim(test, new Dictionary<string, string?>(), args);

    internal static (int ExitCode, string Output) RunShim(TestWorkspace test, Dictionary<string, string?> environment, params string[] args)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "batchpad.com"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        startInfo.Environment[App.DataDirectoryVariable] = test.Paths.DataDirectory;
        startInfo.Environment.Remove("CLAUDECODE");
        foreach (var (name, value) in environment)
            startInfo.Environment[name] = value;
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(Limit))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("batchpad.com did not exit.");
        }
        return (process.ExitCode, output + error.Result);
    }

    private static (CliRunner Runner, StringWriter Output, StringWriter Error) Runner(TestWorkspace test, FakeLauncher launcher,
        Func<string, string?>? environment = null)
    {
        var settings = new Settings();
        new TrustStore(settings, test.Paths.SettingsFile).Trust(Fixture);
        var output = new StringWriter();
        var error = new StringWriter();
        return (new CliRunner(test.Paths, settings, output, error, launcher) { EnvironmentVariable = environment ?? (_ => null) }, output, error);
    }
}
