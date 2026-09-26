using System.Text.Json.Nodes;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class UnattendedTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private const string Scripts = """
        { "id": "deploy", "path": "mark.bat", "confirm": "Deploy to production?" },
        { "id": "pick", "path": "mark.bat", "params": [ { "name": "app", "label": "App", "type": "text", "ask": true } ] },
        { "id": "login", "path": "mark.bat", "params": [ { "name": "token", "type": "secret" } ] },
        { "id": "flow", "steps": [ { "run": "pick", "confirm": true } ] }
        """;

    [TestMethod]
    public void AConfirmScriptFailsWithoutStartingAProcess()
    {
        using var workspace = Workspace();

        var error = Assert.Throws<RunException>(() => Start(workspace, "deploy"));

        StringAssert.Contains(error.Message, "needs confirmation");
        Assert.IsFalse(File.Exists(workspace.Temp.Path("ran.txt")));
    }

    [TestMethod]
    public async Task AConfirmedConfirmScriptRuns()
    {
        using var workspace = Workspace();

        using var run = Start(workspace, "deploy", confirmed: true);

        Assert.IsTrue((await run.Completion.WaitAsync(Limit)).Succeeded);
    }

    [TestMethod]
    public async Task AnAskParameterSuppliedByValuesProceeds()
    {
        using var workspace = Workspace();

        using var run = Start(workspace, "pick", new() { ["app"] = "editor" });

        Assert.IsTrue((await run.Completion.WaitAsync(Limit)).Succeeded);
        Assert.IsTrue(run.Output.Any(l => l.Text == "editor"));
    }

    [TestMethod]
    public void AMissingAskOrSecretValueFailsNamingIt()
    {
        using var workspace = Workspace();

        Assert.AreEqual("'pick' needs a value for App.", Assert.Throws<RunException>(() => Start(workspace, "pick")).Message);
        StringAssert.Contains(Assert.Throws<RunException>(() => Start(workspace, "login")).Message, "needs a value for token");
        Assert.IsFalse(File.Exists(workspace.Temp.Path("ran.txt")));
    }

    [TestMethod]
    public void AnUnattendedWorkflowWithAConfirmStepFails()
    {
        using var workspace = Workspace();
        var loaded = workspace.Workspace;
        var workflow = (WorkflowNode)loaded.References.Resolve("flow", TreeKind.Workspace)!;
        var runner = new WorkflowRunner(loaded, workspace.Gate, RunWorkspace.Interpreters);

        var error = Assert.Throws<WorkflowException>(() => runner.Start(new WorkflowRequest(loaded.Workspace, workflow) { Unattended = true }));

        StringAssert.Contains(error.Message, "needs confirmation");
    }

    [TestMethod]
    public void SecretsAreMaskedLongestFirst()
    {
        using var workspace = Workspace();
        var request = workspace.Request("login", new() { ["token"] = "abc123" });

        Assert.AreEqual($"login --token {SecretMasker.Placeholder}",
            SecretMasker.Mask("login --token abc123", SecretMasker.SecretValues(request)));
    }

    private static RunWorkspace Workspace()
    {
        var workspace = new RunWorkspace(Scripts);
        File.WriteAllText(workspace.Temp.Path("mark.bat"), "@echo off\r\necho ran> \"%~dp0ran.txt\"\r\nif not \"%~1\"==\"\" echo %~1\r\n");
        return workspace;
    }

    private static RunHandle Start(RunWorkspace workspace, string id, Dictionary<string, JsonNode?>? values = null, bool confirmed = false) =>
        workspace.Gate.Start(workspace.Request(id, values) with { Unattended = true, Confirmed = confirmed }, RunWorkspace.Interpreters);
}
