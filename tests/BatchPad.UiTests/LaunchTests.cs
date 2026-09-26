namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class LaunchTests
{
    [TestMethod]
    public void MainWindowOpensWithTitle()
    {
        using var app = AppLauncher.Start();
        var window = app.MainWindow();
        Assert.AreEqual("BatchPad", window.Title);
        window.Close();
        Assert.IsTrue(app.WaitForExit(TimeSpan.FromSeconds(10)), "BatchPad did not exit after its window closed.");
    }
}
