using BatchPad.App.ViewModels.Workflows;
using BatchPad.Core.Workflows;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class WorkflowRerunTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    [TestMethod]
    public async Task AFailedRunOffersARerunFromTheFailedStepThatReusesTheStepsBeforeIt()
    {
        using var test = new TestWorkspace();
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "ws")).FullName;
        File.WriteAllText(Path.Combine(directory, "version.bat"), "@echo ran>>version.log\r\n@echo ::set version=1.2.3\r\n");
        File.WriteAllText(Path.Combine(directory, "flaky.bat"),
            "@if exist flaky.marker (echo ok) else (echo failed>flaky.marker & exit /b 1)\r\n");
        File.WriteAllText(Path.Combine(directory, "show.bat"), "@echo %~1\r\n");
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            { "id": "rerun", "scripts": [
              { "id": "version", "name": "Version", "path": "version.bat" },
              { "id": "flaky", "name": "Flaky", "path": "flaky.bat" },
              { "id": "show", "name": "Show", "path": "show.bat", "params": [ { "name": "x", "type": "text" } ] },
              { "id": "flow", "name": "Flow", "steps": [
                { "id": "ver", "run": "version" },
                { "id": "flaky", "run": "flaky" },
                { "id": "show", "run": "show", "values": { "x": "${steps.ver.version}" } } ] } ] }
            """);
        var main = test.OpenMain(directory, trusted: true);
        main.Tree!.Find("Workspace/Flow")!.IsSelected = true;

        main.Details.RunCommand.Execute(null);
        var failed = (WorkflowRunViewModel)main.Output.Tabs.Single();
        await failed.Finished.WaitAsync(Limit);

        Assert.AreEqual("failed", failed.StatusText);
        Assert.IsTrue(failed.CanRerunFromFailed);
        Assert.AreEqual("Re-run from Flaky", failed.RerunLabel);

        failed.RerunFromFailedCommand.Execute(null);
        var resumed = (WorkflowRunViewModel)main.Output.Tabs.Last();
        Assert.AreNotSame(failed, resumed);
        await resumed.Finished.WaitAsync(Limit);

        Assert.AreEqual("passed", resumed.StatusText);
        CollectionAssert.AreEqual(new[] { StepStatus.Reused, StepStatus.Succeeded, StepStatus.Succeeded },
            resumed.Steps.Select(s => s.Status).ToArray());
        Assert.HasCount(1, File.ReadAllLines(Path.Combine(directory, "version.log")));
        await WaitForLineAsync(resumed.Steps[2], "1.2.3");
        Assert.IsFalse(resumed.CanRerunFromFailed);
        Assert.IsFalse(resumed.RerunFromFailedCommand.CanExecute(null));
    }

    private static async Task WaitForLineAsync(StepRowViewModel step, string text)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!step.Lines.Any(l => l.Text == text) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.IsTrue(step.Lines.Any(l => l.Text == text), string.Join("\n", step.Lines.Select(l => l.Text)));
    }
}
