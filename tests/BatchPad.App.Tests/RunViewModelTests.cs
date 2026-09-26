using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class RunViewModelTests
{
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
        var main = new MainViewModel(test.Paths, new Settings(), new FakeLauncher(), shell: new FakeShell());
        main.Open(Path.Combine(TestWorkspace.DemoSource, "batchpad.json"));
        main.Tree!.Find("Workspace/Hello/hello.bat")!.IsSelected = true;

        Assert.IsFalse(main.Details.RunCommand.CanExecute(null));

        main.TrustWorkspaceCommand.Execute(null);

        Assert.IsTrue(main.Details.RunCommand.CanExecute(null));
    }

    private static MainViewModel Open(TestWorkspace test, FakeLauncher launcher, string node, FakeShell? shell = null)
    {
        var main = new MainViewModel(test.Paths, new Settings(), launcher, shell: shell ?? new FakeShell());
        main.Trust.Trust(TestWorkspace.DemoSource);
        main.Open(Path.Combine(TestWorkspace.DemoSource, "batchpad.json"));
        main.Tree!.Find(node)!.IsSelected = true;
        return main;
    }
}

internal sealed class FakeLauncher : IRunLauncher
{
    public List<FakeProcess> Started { get; } = [];
    public List<RunRequest> Requests { get; } = [];
    public Exception? Failure { get; init; }

    public IRunProcess Start(RunRequest request)
    {
        Requests.Add(request);
        if (Failure is not null)
            throw Failure;
        var process = new FakeProcess();
        Started.Add(process);
        return process;
    }
}

internal sealed class FakeProcess : IRunProcess
{
    private readonly TaskCompletionSource<RunResult> _completion = new();
    private Action<OutputLine>? _onLine;

    public Task<RunResult> Completion => _completion.Task;

    public IDisposable Subscribe(Action<OutputLine> onLine)
    {
        _onLine = onLine;
        return this;
    }

    public void Emit(string text, OutputStream stream) => _onLine?.Invoke(new OutputLine(text, stream));

    public void Finish(RunOutcome outcome, int exitCode) =>
        _completion.TrySetResult(new RunResult(outcome, exitCode, TimeSpan.FromSeconds(1)));

    public bool? CompanionStarted { get; private set; }

    public Task StopAsync(Func<bool>? stopCompanion = null)
    {
        CompanionStarted = stopCompanion?.Invoke();
        Finish(RunOutcome.Stopped, -1);
        return Task.CompletedTask;
    }

    public void Dispose() => _onLine = null;
}

internal sealed class FakeShell : IShellService
{
    public string? Copied { get; private set; }
    public List<string> Opened { get; } = [];

    public void CopyText(string text) => Copied = text;
    public void Open(string target) => Opened.Add(target);
}
