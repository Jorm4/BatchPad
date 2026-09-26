using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class OutputNavigationTests
{
    [TestMethod]
    public void ClickingAReferenceOpensItWithTheEditorCommand()
    {
        using var test = new TestWorkspace();
        var shell = new FakeShell();
        var (run, process) = Start(test, shell, new Settings { EditorCommand = "edit \"{file}\" {line}" });

        process.Emit(@"hello.py(3,1): error E1: bad", OutputStream.Stdout);
        process.Emit(@"missing.cpp(3,1): error E1: bad", OutputStream.Stdout);
        run.Lines[0].Links!.Single().OpenCommand.Execute(null);

        var expected = Path.Combine(TestWorkspace.DemoSource, "hello.py");
        CollectionAssert.AreEqual(new[] { $"edit \"{expected}\" 3" }, shell.Commands);
        Assert.IsNull(run.Lines[1].Links!.Single().Location);
    }

    [TestMethod]
    public void WithoutAnEditorOrVsCodeTheShellOpensTheFile()
    {
        var shell = new FakeShell();

        new SourceOpener(shell, new Settings(), codeOnPath: () => false).Open(new SourceLocation(@"C:\w\a.cpp", 3, 1));

        CollectionAssert.AreEqual(new[] { @"C:\w\a.cpp" }, shell.Opened);
        Assert.IsEmpty(shell.Commands);
    }

    [TestMethod]
    public void WithoutAnEditorAScriptOpensInNotepadRatherThanRunning()
    {
        var shell = new FakeShell();

        new SourceOpener(shell, new Settings(), codeOnPath: () => false).Open(new SourceLocation(@"C:\w\build.bat", 3, 1));

        Assert.IsEmpty(shell.Opened);
        CollectionAssert.AreEqual(new[] { @"notepad.exe ""C:\w\build.bat""" }, shell.Commands);
    }

    [TestMethod]
    public void CmdSpecialCharactersInAnEditorPathStayInert()
    {
        const string path = @"C:\w\a&echo INJECTED|b^c%PATH%!x!.py";
        var start = ShellService.CommandStartInfo("echo [!BP_FILE!]", new Dictionary<string, string> { ["BP_FILE"] = path });
        start.RedirectStandardOutput = true;

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        Assert.AreEqual($"[{path}]", output);
        Assert.AreEqual(Environment.SystemDirectory, start.WorkingDirectory);
    }

    [TestMethod]
    public void F8MovesThroughTheErrorLinesAndWraps()
    {
        using var test = new TestWorkspace();
        var (run, process) = Start(test, new FakeShell(), new Settings(), errorPatterns: [@"error C\d"]);
        process.Emit("compiling", OutputStream.Stdout);
        process.Emit("a.cpp(1): error C1", OutputStream.Stdout);
        process.Emit("ok", OutputStream.Stdout);
        process.Emit("warning on stderr", OutputStream.Stderr);
        process.Emit("b.cpp(2): error C2", OutputStream.Stdout);

        var visited = new List<string?>();
        for (var i = 0; i < 4; i++)
        {
            run.Log.NextErrorCommand.Execute(null);
            visited.Add(run.Log.SelectedLine?.Text);
        }
        run.Log.PreviousErrorCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { "a.cpp(1): error C1", "warning on stderr", "b.cpp(2): error C2", "a.cpp(1): error C1" }, visited);
        Assert.AreEqual("b.cpp(2): error C2", run.Log.SelectedLine?.Text);
        Assert.IsFalse(run.AutoScroll);
    }

    [TestMethod]
    public void SearchFiltersToMatchingLinesIncludingNewOnes()
    {
        using var test = new TestWorkspace();
        var (run, process) = Start(test, new FakeShell(), new Settings());
        process.Emit("Building core", OutputStream.Stdout);
        process.Emit("Linking", OutputStream.Stdout);

        run.Log.SearchText = "build";
        process.Emit("Build done", OutputStream.Stdout);
        process.Emit("Done", OutputStream.Stdout);

        CollectionAssert.AreEqual(new[] { "Building core", "Build done" }, run.Log.DisplayedLines.Select(l => l.Text).ToList());
        run.Log.SearchText = "";
        Assert.HasCount(4, run.Log.DisplayedLines);
    }

    private static (RunViewModel, FakeProcess) Start(TestWorkspace test, FakeShell shell, Settings settings, List<string>? errorPatterns = null)
    {
        var launcher = new FakeLauncher();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, settings: settings, launcher: launcher, shell: shell);
        main.Select("Workspace/Hello/hello.bat");
        main.SelectedNode!.Script!.ErrorPatterns = errorPatterns;
        main.Details.RunCommand.Execute(null);
        return ((RunViewModel)main.Output.Tabs.Single(), launcher.Started.Single());
    }
}
