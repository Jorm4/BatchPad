using System.ComponentModel;
using BatchPad.App.Cli;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class CliSecretTests
{
    [TestMethod]
    public void TheParserReadsSecretCommands()
    {
        var command = CliCommand.Parse(["secret", "set", "token", "--global"]);
        Assert.AreEqual(CliVerb.Secret, command.Verb);
        Assert.AreEqual("set", command.Target);
        Assert.AreEqual("token", command.Parameter);
        Assert.IsTrue(command.Global);
        Assert.IsTrue(CliCommand.IsCli(["secret", "list"]));

        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["secret"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["secret", "show", "token"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["secret", "set"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["secret", "list", "token"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["secret", "set", "token", "value"]));
        Assert.Throws<CliUsageException>(() => CliCommand.Parse(["secret", "set", "token", "--global", "-w", "x"]));
    }

    [TestMethod]
    public async Task SetStoresUnderTheWorkspaceOrGlobalTarget()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        var secrets = new FakeSecretStore();
        var (runner, _, error) = Runner(test, secrets, new FakePrompt("s3cret", "g1obal"));

        Assert.AreEqual(0, await runner.RunAsync(["secret", "set", "token"], project));
        Assert.AreEqual(0, await runner.RunAsync(["secret", "set", "api", "--global"], project));

        Assert.AreEqual("s3cret", Scope(test, project).Get(secrets, "token"));
        Assert.AreEqual("g1obal", SecretScope.Global.Get(secrets, "api"));
        Assert.DoesNotContain("'token'", error.ToString());
    }

    [TestMethod]
    public async Task AnUnknownNameIsAWarningNotAnError()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        var secrets = new FakeSecretStore();
        var (runner, _, error) = Runner(test, secrets, new FakePrompt("x"));

        Assert.AreEqual(0, await runner.RunAsync(["secret", "set", "unknown", "-w", project], test.Root));

        StringAssert.Contains(error.ToString(), "warning: no secret parameter 'unknown'");
        Assert.AreEqual("x", Scope(test, project).Get(secrets, "unknown"));
    }

    [TestMethod]
    public async Task ANonInteractiveOrAgentSetRefuses()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        var secrets = new FakeSecretStore();
        var piped = new FakePrompt("s3cret") { IsInteractive = false };
        var (runner, _, error) = Runner(test, secrets, piped);

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["secret", "set", "token"], project));
        StringAssert.Contains(error.ToString(), "redirected");

        var agentPrompt = new FakePrompt("s3cret");
        var (agent, _, agentError) = Runner(test, secrets, agentPrompt, name => name == "CLAUDECODE" ? "1" : null);
        Assert.AreEqual(CliRunner.Failure, await agent.RunAsync(["secret", "set", "token"], project));
        StringAssert.Contains(agentError.ToString(), "coding agent");

        Assert.AreEqual(0, piped.Reads + agentPrompt.Reads);
        Assert.IsEmpty(secrets.Entries);
    }

    [TestMethod]
    public async Task ListShowsNamesOnlyAndRemoveDeletes()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        var secrets = new FakeSecretStore();
        Scope(test, project).Set(secrets, "token", "hunter2");
        SecretScope.Global.Set(secrets, "api", "hunter3");
        var (runner, output, error) = Runner(test, secrets, new FakePrompt() { IsInteractive = false });

        Assert.AreEqual(0, await runner.RunAsync(["secret", "list"], project));
        Assert.AreEqual("token", output.ToString().Trim());
        Assert.DoesNotContain("hunter", output.ToString());

        Assert.AreEqual(0, await runner.RunAsync(["secret", "remove", "token"], project));
        Assert.IsNull(Scope(test, project).Get(secrets, "token"));
        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["secret", "remove", "token"], project));
        StringAssert.Contains(error.ToString(), "No secret 'token'");
        Assert.AreEqual("hunter3", SecretScope.Global.Get(secrets, "api"));
    }

    [TestMethod]
    public async Task ASavedSecretLetsAnUnattendedRunGoAhead()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        TrustStore.Load(test.Paths).Trust(project);
        var secrets = new FakeSecretStore();
        var launcher = new FakeLauncher();
        var (runner, _, error) = Runner(test, secrets, new FakePrompt("s3cret"), launcher: launcher);

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["run", "deploy"], project));
        StringAssert.Contains(error.ToString(), "needs a value for token");

        Assert.AreEqual(0, await runner.RunAsync(["secret", "set", "token"], project));
        var running = runner.RunAsync(["run", "deploy"], project);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);

        Assert.AreEqual(0, await running);
        Assert.AreEqual("s3cret", launcher.Requests.Single().Values!["token"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task AnAgentRunGetsNoSavedSecret()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        TrustStore.Load(test.Paths).Trust(project);
        var secrets = new FakeSecretStore();
        Scope(test, project).Set(secrets, "token", "s3cret");

        foreach (var (args, variable) in new (string[], string)[]
                 {
                     (["run", "deploy", "--agent", "tester"], ""),
                     (["run", "deploy"], "CLAUDECODE"),
                     (["run", "deploy"], CliTrust.McpVariable),
                 })
        {
            var launcher = new FakeLauncher();
            var (runner, _, error) = Runner(test, secrets, new FakePrompt(), name => name == variable ? "1" : null, launcher);

            Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(args, project), variable);
            StringAssert.Contains(error.ToString(), "needs a value for token");
            StringAssert.Contains(error.ToString(), "Saved secrets aren't given to runs a coding agent starts");
            Assert.IsEmpty(launcher.Requests);
        }
    }

    [TestMethod]
    public async Task ACredentialManagerFailureIsReportedWithoutACrash()
    {
        using var test = new TestWorkspace();
        var project = Project(test);
        TrustStore.Load(test.Paths).Trust(project);
        var secrets = new FakeSecretStore { Failure = new Win32Exception(1312, "Could not read 'token': no logon session.") };
        var (runner, _, error) = Runner(test, secrets, new FakePrompt(), launcher: new FakeLauncher());

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["run", "deploy"], project));

        Assert.AreEqual("Could not read 'token': no logon session.", error.ToString().Trim().Split(Environment.NewLine)[^1]);
    }

    [TestMethod]
    public async Task AWorkflowStepGetsItsSavedSecret()
    {
        using var test = new TestWorkspace();
        var project = Path.Combine(test.Root, "flow");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "check.bat"), "@if \"%~1\"==\"s3cret\" (echo ok) else (exit /b 5)\r\n");
        File.WriteAllText(Path.Combine(project, WorkspaceLocator.FileName), """
            { "id": "flow-test", "scripts": [
              { "id": "check", "path": "check.bat", "params": [ { "name": "token", "type": "secret" } ] },
              { "id": "release", "steps": [ { "run": "check" } ] } ] }
            """);
        TrustStore.Load(test.Paths).Trust(project);
        var secrets = new FakeSecretStore();
        var (runner, _, error) = Runner(test, secrets, new FakePrompt());

        Assert.AreEqual(CliRunner.Failure, await runner.RunAsync(["run", "release"], project));
        StringAssert.Contains(error.ToString(), "'check' needs a value for token");
        Assert.DoesNotContain("coding agent", error.ToString());
        Scope(test, project).Set(secrets, "token", "s3cret");
        Assert.AreEqual(0, await runner.RunAsync(["run", "release"], project), error.ToString());
    }

    private static string Project(TestWorkspace test)
    {
        var project = Path.Combine(test.Root, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, WorkspaceLocator.FileName), """
            { "id": "secret-test", "scripts": [
              { "id": "deploy", "command": "echo", "params": [ { "name": "token", "type": "secret" } ] } ] }
            """);
        return project;
    }

    private static SecretScope Scope(TestWorkspace test, string project) =>
        SecretScope.Of(WorkspaceLoader.Load(Path.Combine(project, WorkspaceLocator.FileName), test.Paths));

    private static (CliRunner Runner, StringWriter Output, StringWriter Error) Runner(TestWorkspace test, FakeSecretStore secrets,
        FakePrompt prompt, Func<string, string?>? environment = null, FakeLauncher? launcher = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var runner = new CliRunner(test.Paths, Settings.Load(test.Paths.SettingsFile), output, error, launcher)
        {
            EnvironmentVariable = environment ?? (_ => null),
            Prompt = prompt,
            Secrets = secrets,
        };
        return (runner, output, error);
    }
}
