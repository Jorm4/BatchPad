using BatchPad.App.ViewModels;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class CloseWithRunsTests
{
    [TestMethod]
    public void ClosingWithoutRunsDoesNotAsk()
    {
        using var test = new TestWorkspace();
        var (main, confirm, _) = Open(test);

        Assert.AreEqual(CloseAction.Close, main.ConfirmClose());
        Assert.IsEmpty(confirm.Asked);
    }

    [TestMethod]
    public async Task ClosingWithARunAsksAndCancelKeepsTheWindowOpen()
    {
        using var test = new TestWorkspace();
        var (main, confirm, launcher) = Open(test);
        main.Details.RunCommand.Execute(null);

        Assert.AreEqual(CloseAction.Cancel, main.ConfirmClose());
        Assert.HasCount(1, confirm.Asked);
        StringAssert.Contains(confirm.Asked[0], "A run is still running");

        confirm.CancellableAnswer = false;
        Assert.AreEqual(CloseAction.Close, main.ConfirmClose());

        confirm.CancellableAnswer = true;
        Assert.AreEqual(CloseAction.StopThenClose, main.ConfirmClose());
        await main.StopAllAsync();
        Assert.IsTrue(launcher.Started.Single().Completion.IsCompleted);
    }

    private static (MainViewModel, FakeConfirm, FakeLauncher) Open(TestWorkspace test)
    {
        var confirm = new FakeConfirm();
        var launcher = new FakeLauncher();
        var main = new MainViewModel(test.Paths, new Settings(), launcher, shell: new FakeShell(), confirm: confirm);
        main.Trust.Trust(TestWorkspace.DemoSource);
        main.Open(Path.Combine(TestWorkspace.DemoSource, "batchpad.json"));
        main.Tree!.Find("Workspace/Hello/hello.bat")!.IsSelected = true;
        return (main, confirm, launcher);
    }
}
