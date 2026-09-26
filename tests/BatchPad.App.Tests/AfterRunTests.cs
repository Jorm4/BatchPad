using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class AfterRunTests
{
    private const string Server = "Workspace/Web/Serve demo";

    [TestMethod]
    public void ReadyPatternOpenUrlAndAdvancedFieldsRoundTrip()
    {
        using var test = new TestWorkspace();
        var main = OpenDemo(test, new FakeLauncher(), new FakeShell());
        var editor = Edit(main, "Workspace/Hello/hello.py");

        editor.AfterRun.ReadyPattern = @"Listening on port (\d+)";
        editor.AfterRun.OpenUrl = "http://localhost:$1/";
        editor.AfterRun.Stop = editor.AfterRun.StopChoices.Single(c => c.Value == "stop-serve");
        editor.AfterRun.TestReport = "build/junit.xml";
        editor.Advanced.FixedArgs = "--fast \"two words\"";
        editor.Advanced.NameTemplate = "Hello ${param:x}";
        editor.SaveCommand.Execute(null);

        var saved = Entry(main, "hello-py");
        Assert.AreEqual(@"Listening on port (\d+)", saved.Ready!.Pattern);
        Assert.AreEqual("http://localhost:$1/", saved.Ready.Open);
        Assert.AreEqual("stop-serve", saved.Stop);
        Assert.AreEqual("build/junit.xml", saved.TestReport?.Path);
        CollectionAssert.AreEqual(new[] { "--fast", "two words" }, saved.Args);
        Assert.AreEqual("Hello ${param:x}", saved.NameTemplate);

        var reopened = Edit(main, "Workspace/Hello/hello.py");
        Assert.AreEqual(@"Listening on port (\d+)", reopened.AfterRun.ReadyPattern);
        Assert.AreEqual("http://localhost:$1/", reopened.AfterRun.OpenUrl);
        Assert.AreEqual("--fast \"two words\"", reopened.Advanced.FixedArgs);
    }

    [TestMethod]
    public async Task RunningTheDemoServerFlipsTheBadgeToReadyAndStopAsksTheCompanion()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var shell = new FakeShell();
        var main = OpenDemo(test, launcher, shell);
        var node = main.Select(Server);
        ((IntFieldViewModel)main.Details.Form!.Field("port")!).Text = "9001";

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        Assert.IsFalse(run.IsReady);
        launcher.Started[0].Emit("Serving on http://127.0.0.1:9001/", OutputStream.Stdout);
        await Eventually(() => run.IsReady && shell.Opened.Count > 0);

        Assert.AreEqual("http://127.0.0.1:9001/", run.ReadyUrl);
        Assert.AreEqual("running · ready", run.StatusText);
        Assert.AreEqual(RunBadge.Ready, node.Badge);
        CollectionAssert.AreEqual(new[] { "http://127.0.0.1:9001/" }, shell.Opened);
        run.OpenInBrowserCommand.Execute(null);
        Assert.HasCount(2, shell.Opened);

        await main.Details.StopCommand.ExecuteAsync(null);
        await run.Finished;

        Assert.IsTrue(launcher.Started[0].CompanionStarted);
        var companion = launcher.Requests.Last();
        Assert.AreEqual("stop-serve", companion.Script.Id);
        Assert.AreEqual(9001, companion.Values!["port"]!.GetValue<int>());
        Assert.AreEqual("stopped", run.StatusText);
        Assert.AreEqual("Stop demo server", main.Output.Tabs.Last().Title);
    }

    [TestMethod]
    public async Task ArtifactsShowAsLinksAndOpenOnSuccess()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var shell = new FakeShell();
        var main = OpenDemo(test, launcher, shell);
        var editor = Edit(main, "Workspace/Hello/hello.bat");
        editor.AfterRun.AddArtifactCommand.Execute(null);
        editor.AfterRun.Artifacts[0].Path = "batchpad.json";
        editor.AfterRun.Artifacts[0].Open = editor.AfterRun.OpenOptions.Single(o => o.Value == ArtifactOpen.OnSuccess);
        editor.SaveCommand.Execute(null);

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        launcher.Started[0].Finish(RunOutcome.Exited, 0);
        await run.Finished;

        var expected = Path.Combine(main.Workspace!.Directory, "batchpad.json");
        Assert.AreEqual("batchpad.json", run.Artifacts.Single().Name);
        Assert.IsTrue(run.Artifacts[0].OpenCommand.CanExecute(null));
        CollectionAssert.AreEqual(new[] { expected }, shell.Opened);
    }

    [TestMethod]
    public async Task TheRealDemoServerBecomesReadyAndStopsThroughItsCompanion()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var shell = new FakeShell();
        var main = test.OpenMain(demo, trusted: true, shell: shell, confirm: new FakeConfirm());
        main.Select(Server);
        var port = FreePort();
        ((IntFieldViewModel)main.Details.Form!.Field("port")!).Text = port.ToString();

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        await Eventually(() => run.IsReady || !run.IsRunning);
        Assert.AreEqual($"http://127.0.0.1:{port}/", run.ReadyUrl, string.Join(Environment.NewLine, run.Lines));

        await main.Details.StopCommand.ExecuteAsync(null);
        await run.Finished.WaitAsync(Limit);
        var companion = (RunViewModel)main.Output.Tabs.Last();
        await companion.Finished.WaitAsync(Limit);

        Assert.AreEqual("stopped", run.StatusText);
        Assert.IsTrue(companion.Lines.Any(l => l.Text == $"Asked port {port} to stop."), string.Join(Environment.NewLine, companion.Lines));
        Assert.IsTrue(run.Lines.Any(l => l.Text == "Stopped."), string.Join(Environment.NewLine, run.Lines));
    }

    private static MainViewModel OpenDemo(TestWorkspace test, FakeLauncher launcher, FakeShell shell)
    {
        var demo = test.CopyDemo();
        var main = test.OpenMain(demo, trusted: true, launcher: launcher, shell: shell, confirm: new FakeConfirm());
        return main;
    }

    private static ScriptEditorViewModel Edit(MainViewModel main, string automationId)
    {
        main.Select(automationId);
        main.Details.EditCommand.Execute(null);
        return main.Details.Editor!;
    }

    private static ScriptNode Entry(MainViewModel main, string id) =>
        ConfigReader.ReadFile(main.Workspace!.FilePath).Scripts
            .SelectMany(n => n is FolderNode f ? f.Items : [n]).OfType<ScriptNode>().Single(s => s.Id == id);

}
