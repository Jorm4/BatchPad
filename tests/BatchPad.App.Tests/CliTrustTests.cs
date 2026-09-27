using BatchPad.App.Cli;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class CliTrustTests
{
    [TestMethod]
    public void TheParserReadsTrustVerbs()
    {
        Assert.IsTrue(CliCommand.IsCli(["trust"]));
        Assert.IsTrue(CliCommand.IsCli(["untrust"]));
        Assert.IsTrue(CliCommand.Parse(["trust", "--list"]).List);
        Assert.AreEqual("x", CliCommand.Parse(["trust", "--agent", "x"]).Agent);
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["untrust", "--list"]));
    }

    [TestMethod]
    public async Task TypingTheFolderNameTrustsItAndARunThenGoesAhead()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        var launcher = new FakeLauncher();
        var (runner, output, _) = Runner(test, new FakePrompt("My-Project"), launcher);

        Assert.AreEqual(0, await runner.RunAsync(["trust"], project));

        StringAssert.Contains(output.ToString(), $"Trust {project}?");
        Assert.IsTrue(TrustStore.Load(test.Paths).IsTrusted(project));
        var running = runner.RunAsync(["run", "hello", "-w", project], test.Root);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        Assert.AreEqual(0, await running);
    }

    [TestMethod]
    public async Task TheRepositoryOriginIsShown()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        File.WriteAllText(Path.Combine(project, ".git", "config"), """
            [core]
            	bare = false
            [remote "upstream"]
            	url = https://example.com/other.git
            [remote "origin"]
            	url = https://example.com/team/project.git
            	fetch = +refs/heads/*:refs/remotes/origin/*
            """);
        var (runner, output, _) = Runner(test, new FakePrompt("nope"));

        await runner.RunAsync(["trust"], project);

        StringAssert.Contains(output.ToString(), "origin: https://example.com/team/project.git");
    }

    [TestMethod]
    public async Task AWrongNameLeavesItUntrusted()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        var (runner, _, error) = Runner(test, new FakePrompt("y"));

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["trust", "--workspace", project], test.Root));

        StringAssert.Contains(error.ToString(), "Not trusted");
        Assert.IsFalse(TrustStore.Load(test.Paths).IsTrusted(project));
    }

    [TestMethod]
    public async Task NobodyAtTheKeyboardOrAnAgentIsRefusedWithoutAsking()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        var cases = new (FakePrompt Prompt, Func<string, string?> Environment, string[] Extra, string Reason)[]
        {
            (new FakePrompt("my-project") { IsInteractive = false }, _ => null, [], "redirected"),
            (new FakePrompt("my-project"), name => name == "CLAUDECODE" ? "1" : null, [], "coding agent"),
            (new FakePrompt("my-project"), _ => null, ["--agent", "bot"], "coding agent"),
            (new FakePrompt("my-project"), name => name == CliTrust.McpVariable ? "1" : null, [], "coding agent"),
        };
        foreach (var (prompt, environment, extra, reason) in cases)
        {
            var (runner, _, error) = Runner(test, prompt, environment: environment);

            Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["trust", "-w", project, .. extra], test.Root));

            StringAssert.Contains(error.ToString(), reason);
            Assert.AreEqual(0, prompt.Reads);
            Assert.IsFalse(TrustStore.Load(test.Paths).IsTrusted(project));
        }
    }

    [TestMethod]
    public async Task ListShowsTrustedFoldersAndUntrustRemovesOne()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        TrustStore.Load(test.Paths).Trust(project);
        var (runner, output, _) = Runner(test, new FakePrompt());

        Assert.AreEqual(0, await runner.RunAsync(["trust", "--list"], test.Root));
        StringAssert.Contains(output.ToString(), project);

        Assert.AreEqual(0, await runner.RunAsync(["untrust"], project));
        Assert.IsFalse(TrustStore.Load(test.Paths).IsTrusted(project));
        Assert.IsEmpty(TrustStore.Load(test.Paths).TrustedFolders);
    }

    [TestMethod]
    public void PipedTrustThroughTheShimIsRefused()
    {
        using var test = new TestWorkspace();
        var project = Project(test);

        var (code, output) = CliTests.RunShim(test, "trust", "--workspace", project);

        Assert.AreEqual(CliRunner.Failure, code, output);
        StringAssert.Contains(output, "input is redirected");
        Assert.IsFalse(TrustStore.Load(test.Paths).IsTrusted(project));
    }

    internal static string Project(TestWorkspace test)
    {
        var project = Path.Combine(test.Root, "my-project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, WorkspaceLocator.FileName), """
            { "id": "trust-test", "scripts": [ { "id": "hello", "command": "echo hi" } ] }
            """);
        return project;
    }

    private static (CliRunner Runner, StringWriter Output, StringWriter Error) Runner(TestWorkspace test, FakePrompt prompt,
        FakeLauncher? launcher = null, Func<string, string?>? environment = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliRunner(test.Paths, Settings.Load(test.Paths.SettingsFile), output, error, launcher)
        {
            EnvironmentVariable = environment ?? (_ => null),
            Prompt = prompt,
            Secrets = new FakeSecretStore(),
        };
        return (runner, output, error);
    }
}
