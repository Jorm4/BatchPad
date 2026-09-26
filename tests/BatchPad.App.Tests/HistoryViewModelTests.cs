using System.Text.Json.Nodes;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.History;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class HistoryViewModelTests
{
    [TestMethod]
    public async Task AFailedRunStillShowsAsFailedAfterReopening()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.py");

        main.Details.RunCommand.Execute(null);
        launcher.Started.Single().Finish(RunOutcome.Exited, 3);
        await main.Output.Tabs.Single().Finished;

        var reopened = Open(test, new FakeLauncher(), "Workspace/Hello/hello.py");

        Assert.AreEqual(RunBadge.Failed, reopened.SelectedNode!.Badge);
        Assert.AreEqual(RunBadge.None, reopened.Tree!.Find("Workspace/Hello/hello.bat")!.Badge);
    }

    [TestMethod]
    public async Task TheHistoryListShowsTheRunWithItsTrigger()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.bat");

        main.Details.RunCommand.Execute(null);
        var process = launcher.Started.Single();
        process.Emit("Hello", OutputStream.Stdout);
        process.Finish(RunOutcome.Exited, 0);
        await main.Output.Tabs.Single().Finished;

        var entry = main.History.Runs.Single();
        Assert.AreEqual("hello.bat", entry.Name);
        Assert.AreEqual(RunTriggers.Manual, entry.Trigger);
        Assert.AreEqual("exit 0", entry.ResultText);
        Assert.IsTrue(entry.Succeeded);
        Assert.AreEqual("Hello", File.ReadAllText(main.History.Store!.LogPath(entry.Record)).Trim());
    }

    [TestMethod]
    public async Task RunAgainStartsARequestWithTheRecordedValues()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Parameters demo", main =>
            main.SessionValues["Workspace:id:params-demo"] = new ParameterValues(new Dictionary<string, JsonNode?> { ["game"] = "Gamma" }, ""));

        main.Details.RunCommand.Execute(null);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        await main.Output.Tabs.Single().Finished;

        var againLauncher = new FakeLauncher();
        var reopened = Open(test, againLauncher, "Workspace/Hello/hello.bat");
        reopened.History.Runs.Single().RunAgainCommand.Execute(null);

        var request = againLauncher.Requests.Single();
        Assert.AreEqual("params-demo", request.Script.Id);
        Assert.AreEqual("Gamma", request.Values!["game"]!.GetValue<string>());
        Assert.AreEqual("Parameters demo", reopened.SelectedNode!.Name);
    }

    private static MainViewModel Open(TestWorkspace test, FakeLauncher launcher, string node, Action<MainViewModel>? beforeSelecting = null)
    {
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: launcher, shell: new FakeShell());
        beforeSelecting?.Invoke(main);
        main.Select(node);
        return main;
    }
}
