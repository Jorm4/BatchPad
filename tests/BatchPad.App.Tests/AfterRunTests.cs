using System.Text.Json.Nodes;
using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

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
        Assert.AreEqual("build/junit.xml", saved.TestReport);
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
        var node = Select(main, Server);
        ((IntFieldViewModel)main.Details.Form!.Field("port")!).Text = "9001";

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        Assert.IsFalse(run.IsReady);
        launcher.Started[0].Emit("Serving on http://127.0.0.1:9001/", OutputStream.Stdout);
        await Until(() => run.IsReady && shell.Opened.Count > 0);

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
        editor.AfterRun.Artifacts[0].Path = "hello.py";
        editor.AfterRun.Artifacts[0].Open = editor.AfterRun.OpenOptions.Single(o => o.Value == ArtifactOpen.OnSuccess);
        editor.SaveCommand.Execute(null);

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        launcher.Started[0].Finish(RunOutcome.Exited, 0);
        await run.Finished;

        var expected = Path.Combine(main.Workspace!.Directory, "hello.py");
        Assert.AreEqual("hello.py", run.Artifacts.Single().Name);
        Assert.IsTrue(run.Artifacts[0].OpenCommand.CanExecute(null));
        CollectionAssert.AreEqual(new[] { expected }, shell.Opened);
    }

    [TestMethod]
    public async Task TheRealDemoServerBecomesReadyAndStopsThroughItsCompanion()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var shell = new FakeShell();
        var main = new MainViewModel(test.Paths, new Settings(), shell: shell, confirm: new FakeConfirm());
        main.Trust.Trust(demo);
        main.OpenInitial(demo, test.Root);
        Select(main, Server);
        var port = FreePort();
        ((IntFieldViewModel)main.Details.Form!.Field("port")!).Text = port.ToString();

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        await Until(() => run.IsReady || !run.IsRunning, TimeSpan.FromSeconds(30));
        Assert.AreEqual($"http://127.0.0.1:{port}/", run.ReadyUrl, string.Join(Environment.NewLine, run.Lines));

        await main.Details.StopCommand.ExecuteAsync(null);
        await run.Finished.WaitAsync(TimeSpan.FromSeconds(30));
        var companion = (RunViewModel)main.Output.Tabs.Last();
        await companion.Finished.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.AreEqual("stopped", run.StatusText);
        Assert.IsTrue(companion.Lines.Any(l => l.Text == $"Asked port {port} to stop."), string.Join(Environment.NewLine, companion.Lines));
        Assert.IsTrue(run.Lines.Any(l => l.Text == "Stopped."), string.Join(Environment.NewLine, run.Lines));
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static MainViewModel OpenDemo(TestWorkspace test, FakeLauncher launcher, FakeShell shell)
    {
        var demo = test.CopyDemo();
        var main = new MainViewModel(test.Paths, new Settings(), launcher, shell: shell, confirm: new FakeConfirm());
        main.Trust.Trust(demo);
        main.OpenInitial(demo, test.Root);
        return main;
    }

    private static NodeViewModel Select(MainViewModel main, string automationId)
    {
        var node = main.Tree!.Find(automationId)!;
        node.IsSelected = true;
        return node;
    }

    private static ScriptEditorViewModel Edit(MainViewModel main, string automationId)
    {
        Select(main, automationId);
        main.Details.EditCommand.Execute(null);
        return main.Details.Editor!;
    }

    private static ScriptNode Entry(MainViewModel main, string id) =>
        ConfigReader.ReadFile(main.Workspace!.FilePath).Scripts
            .SelectMany(n => n is FolderNode f ? f.Items : [n]).OfType<ScriptNode>().Single(s => s.Id == id);

    private static async Task Until(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for the run to become ready.");
            await Task.Delay(10);
        }
    }
}
