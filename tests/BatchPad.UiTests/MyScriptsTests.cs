using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class MyScriptsTests
{
    [TestMethod]
    public void AddToMyScriptsFromTheContextMenuAddsAMyScriptsItem()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        var item = app.WaitFor(window, "Workspace/Hello/hello.py").AsTreeItem();
        item.Select();

        item.RightClick();
        var menuItem = app.WaitFor(app.Automation.GetDesktop(), "AddToMyScriptsMenuItem").AsMenuItem();
        menuItem.Invoke();

        var added = app.WaitFor(window, "MyScripts/hello.py").AsTreeItem();
        Assert.IsTrue(added.IsSelected);
        Assert.AreEqual("hello.py", app.WaitFor(window, "DetailsName").Name);
    }
}
