using System.Diagnostics;
using BatchPad.App.Cli;
using BatchPad.Core.History;
using BatchPad.Core.Running;
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
        CollectionAssert.AreEqual(new[] { "exit-three\tExit three", "deploy\tDeploy", "flow\tFlow" },
            output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual(CliRunner.UsageError, await runner.RunAsync(["run", "missing", "-w", Fixture], test.Root));
    }

    [TestMethod]
    public void TheComShimRunsScriptsAndPassesTheExitCode()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var settings = new Settings();
        var trust = new TrustStore(settings, test.Paths.SettingsFile);
        trust.Trust(demo);
        trust.Trust(Fixture);

        var (hello, helloOutput) = RunShim(test, "run", "hello-bat", "--workspace", demo);
        Assert.AreEqual(0, hello, helloOutput);
        StringAssert.Contains(helloOutput, "Hello from batch!");

        var (three, threeOutput) = RunShim(test, "run", "exit-three", "--workspace", Fixture);
        Assert.AreEqual(3, three, threeOutput);

        var (flow, flowOutput) = RunShim(test, "run", "flow", "--workspace", Fixture);
        Assert.AreEqual(3, flow, flowOutput);
    }

    internal static (int ExitCode, string Output) RunShim(TestWorkspace test, params string[] args)
    {
        var startInfo = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "batchpad.com"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment[App.DataDirectoryVariable] = test.Paths.DataDirectory;
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("batchpad.com did not exit.");
        }
        return (process.ExitCode, output + error.Result);
    }

    private static (CliRunner Runner, StringWriter Output, StringWriter Error) Runner(TestWorkspace test, FakeLauncher launcher)
    {
        var settings = new Settings();
        new TrustStore(settings, test.Paths.SettingsFile).Trust(Fixture);
        var output = new StringWriter();
        var error = new StringWriter();
        return (new CliRunner(test.Paths, settings, output, error, launcher), output, error);
    }
}
