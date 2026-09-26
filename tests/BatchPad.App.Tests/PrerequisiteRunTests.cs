using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Workflows;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class PrerequisiteRunTests
{
    [TestMethod]
    public async Task RunningAScriptWithAFailingPrerequisiteShowsItFailedOnTheTab()
    {
        using var test = new TestWorkspace();
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "ws")).FullName;
        File.WriteAllText(Path.Combine(directory, "gen.bat"), "@exit /b 3\r\n");
        File.WriteAllText(Path.Combine(directory, "build.bat"), "@echo build\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            { "id": "deps", "scripts": [
              { "id": "gen", "name": "Generate", "path": "gen.bat" },
              { "id": "build", "name": "Build", "path": "build.bat", "dependsOn": ["gen"] } ] }
            """);
        var main = test.OpenMain(directory, trusted: true);
        var node = main.Tree!.Find("Workspace/Build")!;
        node.IsSelected = true;

        StringAssert.StartsWith(main.Details.Preview, "Generate → Build");
        main.Details.RunCommand.Execute(null);

        var tab = (WorkflowRunViewModel)main.Output.Tabs.Single();
        await tab.Finished.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("failed", tab.StatusText);
        CollectionAssert.AreEqual(new[] { "Generate", "Build" }, tab.Steps.Select(s => s.Name).ToArray());
        CollectionAssert.AreEqual(new[] { StepStatus.Failed, StepStatus.Skipped }, tab.Steps.Select(s => s.Status).ToArray());
        Assert.AreEqual(RunBadge.Failed, node.Badge);
    }
}
