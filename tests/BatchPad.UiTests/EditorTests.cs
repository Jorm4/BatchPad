using FlaUI.Core.AutomationElements;

namespace BatchPad.UiTests;

[TestClass]
[TestCategory("UI")]
public sealed class EditorTests
{
    [TestMethod]
    public void EditOpensTheEditorAndCancelReturnsToTheForm()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        app.WaitFor(window, "Workspace/Parameters demo").AsTreeItem().Select();

        app.WaitFor(window, "EditButton").AsButton().Invoke();

        Assert.AreEqual("Parameters demo", app.WaitFor(window, "EditorName").AsTextBox().Text);
        Assert.IsNotNull(app.WaitFor(window, "SharedBanner"));
        app.WaitFor(window, "ParametersTab").AsTabItem().Select();
        Assert.IsNotNull(app.WaitFor(window, "ParameterList"));
        Assert.IsNotNull(app.WaitFor(window, "ParamArg"));

        app.WaitFor(window, "EditorCancelButton").AsButton().Invoke();
        Assert.IsNotNull(app.WaitFor(window, "Param_port"));
    }

    [TestMethod]
    public void TheChoicesTabOpensTheSourcePicker()
    {
        using var app = AppLauncher.Start(AppLauncher.DemoWorkspace);
        var window = app.MainWindow();
        app.WaitFor(window, "TrustButton").AsButton().Invoke();
        app.WaitFor(window, "Workspace/Parameters demo").AsTreeItem().Select();
        app.WaitFor(window, "EditButton").AsButton().Invoke();
        app.WaitFor(window, "ParametersTab").AsTabItem().Select();
        var list = app.WaitFor(window, "ParameterList");
        AppLauncher.WaitUntil(() => list.FindFirstChild(cf => cf.ByName("Game")), "the Game parameter").AsListBoxItem().Select();

        var choicesTab = app.WaitFor(window, "ChoicesTab");
        AppLauncher.WaitUntil(() => choicesTab.IsEnabled ? choicesTab : null, "the Choices tab to enable").AsTabItem().Select();
        Assert.IsNotNull(app.WaitFor(window, "ChoicesPreview"));
        app.WaitFor(window, "AddChoiceSource").AsButton().Invoke();
        app.WaitFor(window, "SourceCard_Lines").AsListBoxItem().Select();

        Assert.IsNotNull(app.WaitFor(window, "PickerFile"));
        Assert.IsFalse(app.WaitFor(window, "SourceCard_Script").IsEnabled);
        app.WaitFor(window, "PickerCancelButton").AsButton().Invoke();
        app.WaitFor(window, "EditorCancelButton").AsButton().Invoke();
    }
}
