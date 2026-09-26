using BatchPad.App.ViewModels;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class CommandPaletteTests
{
    [TestMethod]
    public void EnterRunsTheBestMatch()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher);

        main.Palette.OpenCommand.Execute(null);
        main.Palette.Query = "hello.bat";

        Assert.AreEqual("hello.bat", main.Palette.SelectedItem!.Title);
        Assert.AreEqual("Workspace › Hello", main.Palette.SelectedItem.Detail);
        main.Palette.RunCommand.Execute(null);

        Assert.IsFalse(main.Palette.IsOpen);
        Assert.AreEqual("hello.bat", launcher.Requests.Single().Script.Path);
        Assert.AreEqual("Workspace/Hello/hello.bat", main.SelectedNode!.AutomationId);
        Assert.HasCount(1, main.Output.Tabs);
    }

    [TestMethod]
    public void ShiftEnterOnlySelects()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = Open(test, launcher);

        main.Palette.OpenCommand.Execute(null);
        main.Palette.Query = "serve demo";
        main.Palette.SelectCommand.Execute(null);

        Assert.AreEqual("Serve demo", main.SelectedNode!.Name);
        Assert.IsTrue(main.SelectedNode.Parent!.IsExpanded);
        Assert.IsEmpty(launcher.Requests);
    }

    [TestMethod]
    public void CommandsAreListedAndRun()
    {
        using var test = new TestWorkspace();
        var main = Open(test, new FakeLauncher());

        main.Palette.OpenCommand.Execute(null);
        main.Palette.Query = "workspace settings";
        main.Palette.MoveCommand.Execute("1");
        main.Palette.MoveCommand.Execute("-1");
        main.Palette.RunCommand.Execute(null);

        Assert.IsNotNull(main.WorkspaceSettings);
    }

    private static MainViewModel Open(TestWorkspace test, FakeLauncher launcher)
    {
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: launcher, shell: new FakeShell());
        return main;
    }
}
