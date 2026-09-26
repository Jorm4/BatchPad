using BatchPad.Core.Running;
using BatchPad.Core.Workflows;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class PrerequisiteTests
{
    [TestMethod]
    public async Task AFailingPrerequisiteStopsTheScriptFromRunning()
    {
        using var workspace = Workspace("""
            { "id": "gen", "path": "exit3.bat" },
            { "id": "build", "path": "build.bat", "dependsOn": ["gen"] }
            """);

        var run = Start(workspace, "build");
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(WorkflowOutcome.Failed, result.Outcome);
        Assert.AreEqual(StepStatus.Failed, run.Steps[0].Status);
        Assert.AreEqual(3, run.Steps[0].Result!.ExitCode);
        Assert.AreEqual(StepStatus.Skipped, run.Steps[1].Status);
        Assert.IsNull(run.Steps[1].Handle);
    }

    [TestMethod]
    public async Task APassingPrerequisiteRunsFirstThenTheScript()
    {
        using var workspace = Workspace("""
            { "id": "setup", "path": "gen.bat" },
            { "id": "gen", "path": "gen.bat", "dependsOn": ["setup"] },
            { "id": "build", "path": "build.bat", "dependsOn": ["gen"] }
            """);

        var run = Start(workspace, "build");
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(WorkflowOutcome.Succeeded, result.Outcome, string.Join('\n', run.Steps.Select(s => s.Error)));
        CollectionAssert.AreEqual(new[] { "workspace:setup", "workspace:gen", null }, run.Steps.Select(s => s.Step.Run).ToArray());
        var gen = run.Steps[1].Handle!;
        var build = run.Steps[2].Handle!;
        Assert.AreSame(workspace.Request("build").Script, run.Steps[2].Request!.Script);
        Assert.IsGreaterThanOrEqualTo(gen.StartedAt + gen.Completion.Result.Duration, build.StartedAt);
        Assert.AreEqual("build", build.Output.Single(l => l.Stream == OutputStream.Stdout).Text);
    }

    [TestMethod]
    public void ADependsOnCycleIsALoadErrorNamingTheChain()
    {
        using var workspace = Workspace("""
            { "id": "a", "name": "A", "path": "gen.bat", "dependsOn": ["b"] },
            { "id": "b", "name": "B", "path": "gen.bat", "dependsOn": ["a"] }
            """);

        Assert.IsTrue(workspace.Workspace.Errors.Any(e => e.Message.Contains("A → B → A")),
            string.Join('\n', workspace.Workspace.Errors));
    }

    [TestMethod]
    public void AScriptWithoutDependsOnIsNotAWorkflow()
    {
        using var workspace = Workspace("""{ "id": "build", "path": "build.bat" }""");

        Assert.IsNull(Prerequisites.WorkflowFor(workspace.Request("build")));
    }

    private static RunWorkspace Workspace(string scripts)
    {
        var workspace = new RunWorkspace(scripts, "exit3.bat");
        File.WriteAllText(workspace.Temp.Path("gen.bat"), "@echo off\r\necho gen\r\n");
        File.WriteAllText(workspace.Temp.Path("build.bat"), "@echo off\r\necho build\r\n");
        return workspace;
    }

    private static WorkflowRun Start(RunWorkspace workspace, string id)
    {
        var request = Prerequisites.WorkflowFor(workspace.Request(id))!;
        return new WorkflowRunner(workspace.Workspace, workspace.Gate, RunWorkspace.Interpreters).Start(request);
    }
}
