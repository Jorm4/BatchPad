using BatchPad.App.ViewModels;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class RunViewModelTests
{
    [TestMethod]
    public void ChoosingAScriptShowsItsTab()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.bat");
        main.Details.RunCommand.Execute(null);
        var hello = main.Output.SelectedTab;
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        main.Select("Workspace/Hello/hello.py");
        main.Details.RunCommand.Execute(null);
        var python = main.Output.SelectedTab;

        main.Select("Workspace/Hello/hello.bat");
        Assert.AreSame(hello, main.Output.SelectedTab);

        main.Select("Workspace/Hello/hello.cs");
        Assert.AreSame(hello, main.Output.SelectedTab, "A script without a tab leaves the panel as it is.");

        main.Select("Workspace/Hello/hello.py");
        Assert.AreSame(python, main.Output.SelectedTab);
    }

    [TestMethod]
    public async Task ATabGoesFromRunningToExitAndTheBadgeFollows()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.bat");
        var node = main.SelectedNode!;

        main.Details.RunCommand.Execute(null);

        var run = (RunViewModel)main.Output.Tabs.Single();
        Assert.AreSame(run, main.Output.SelectedTab);
        Assert.AreEqual("running", run.StatusText);
        Assert.AreEqual(RunBadge.Running, node.Badge);
        Assert.IsTrue(main.Details.StopCommand.CanExecute(null));

        var process = launcher.Started.Single();
        process.Emit("Hello", OutputStream.Stdout);
        process.Emit("oops", OutputStream.Stderr);
        process.Finish(RunOutcome.Exited, 0);
        await run.Finished;

        CollectionAssert.AreEqual(new[] { "Hello", "oops" }, run.Lines.Select(l => l.Text).ToList());
        Assert.IsTrue(run.Lines[1].IsError);
        Assert.AreEqual("exit 0", run.StatusText);
        Assert.AreEqual(RunBadge.Passed, node.Badge);
        Assert.IsFalse(main.Details.StopCommand.CanExecute(null));
    }

    [TestMethod]
    public async Task ALineMatchingAnErrorPatternIsMarkedAndAnsiIsParsed()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.bat");
        main.SelectedNode!.Script!.ErrorPatterns = [@"LNK\d{4}"];

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        var process = launcher.Started.Single();
        process.Emit("linking", OutputStream.Stdout);
        process.Emit("main.obj : error LNK2019: unresolved external", OutputStream.Stdout);
        process.Emit("\u001b[32mok\u001b[0m", OutputStream.Stdout);
        process.Finish(RunOutcome.Exited, 0);
        await run.Finished;

        CollectionAssert.AreEqual(new[] { false, true, false }, run.Lines.Select(l => l.IsErrorMatch).ToList());
        Assert.AreEqual("ok", run.Lines[2].Text);
        Assert.AreEqual(AnsiColor.Green, run.Lines[2].Spans!.Single().Color);
    }

    [TestMethod]
    public async Task AQueuedRunShowsTheLockItWaitsForOnItsTabAndBadge()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.bat");
        var node = main.SelectedNode!;

        main.Details.RunCommand.Execute(null);
        var run = (RunViewModel)main.Output.Tabs.Single();
        var process = launcher.Started.Single();

        process.Wait("native-build");
        Assert.AreEqual("waiting for lock native-build", run.StatusText);
        Assert.AreEqual(RunBadge.Waiting, node.Badge);

        process.Wait(null);
        Assert.AreEqual("running", run.StatusText);
        Assert.AreEqual(RunBadge.Running, node.Badge);
        process.Finish(RunOutcome.Exited, 0);
        await run.Finished;
    }

    [TestMethod]
    public async Task AFailureMarksTheNodeAndAStopKeepsTheLastResult()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.py");
        var node = main.SelectedNode!;

        main.Details.RunCommand.Execute(null);
        launcher.Started[0].Finish(RunOutcome.Exited, 3);
        await main.Output.Tabs[0].Finished;
        Assert.AreEqual("exit 3", main.Output.Tabs[0].StatusText);
        Assert.AreEqual(RunBadge.Failed, node.Badge);

        main.Details.RunCommand.Execute(null);
        Assert.AreEqual(RunBadge.Running, node.Badge);
        await main.Details.StopCommand.ExecuteAsync(null);
        await main.Output.Tabs[1].Finished;

        Assert.AreEqual("stopped", main.Output.Tabs[1].StatusText);
        Assert.AreEqual(RunBadge.Failed, node.Badge);
    }

    [TestMethod]
    public void PreviewShowsAShortCommandAndCopiesTheFullOne()
    {
        using var test = new TestWorkspace();
        var shell = new FakeShell();
        var main = Open(test, new FakeLauncher(), "Workspace/Hello/hello.bat", shell);
        var workspaceDir = main.Workspace!.Directory;

        StringAssert.StartsWith(main.Details.Preview, "cmd ");
        StringAssert.Contains(main.Details.Preview, "\"hello.bat\"");
        Assert.DoesNotContain(workspaceDir, main.Details.Preview, StringComparison.OrdinalIgnoreCase);

        main.Details.CopyPreviewCommand.Execute(null);

        StringAssert.Contains(shell.Copied!, Path.Combine(workspaceDir, "hello.bat"));
        StringAssert.Contains(shell.Copied!, "cmd.exe");
    }

    [TestMethod]
    public void RunInWindowAsksForAWindowConsole()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher, "Workspace/Hello/hello.bat");

        main.Details.RunInWindowCommand.Execute(null);

        Assert.AreEqual(ConsoleMode.Window, launcher.Requests.Single().Console);
    }

    [TestMethod]
    public void AStartProblemShowsAFailedTab()
    {
        using var test = new TestWorkspace();
        var main = Open(test, new FakeLauncher { Failure = new RunException("not built yet") }, "Workspace/Hello/hello.bat");

        main.Details.RunCommand.Execute(null);

        var run = (RunViewModel)main.Output.Tabs.Single();
        Assert.AreEqual("failed to start", run.StatusText);
        Assert.AreEqual("not built yet", run.Lines.Single().Text);
        Assert.AreEqual(RunBadge.Failed, main.SelectedNode!.Badge);
    }

    [TestMethod]
    public void NothingRunsUntilTheWorkspaceIsTrusted()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource, launcher: new FakeLauncher(), shell: new FakeShell());
        main.Select("Workspace/Hello/hello.bat");

        Assert.IsFalse(main.Details.RunCommand.CanExecute(null));

        main.TrustWorkspaceCommand.Execute(null);

        Assert.IsTrue(main.Details.RunCommand.CanExecute(null));
    }

    private static MainViewModel Open(TestWorkspace test, FakeLauncher launcher, string node, FakeShell? shell = null)
    {
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: launcher, shell: shell ?? new FakeShell());
        main.Select(node);
        return main;
    }
}

