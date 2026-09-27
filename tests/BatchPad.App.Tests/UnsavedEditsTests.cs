using BatchPad.App.ViewModels;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class UnsavedEditsTests
{
    [TestMethod]
    public void ChoosingAnotherScriptAsksBeforeDroppingAnEditAndKeepsItWhenTold()
    {
        using var test = new TestWorkspace();
        var confirm = new FakeConfirm { Answer = false };
        var main = test.OpenMain(test.CopyDemo(), trusted: true, confirm: confirm);
        var edited = main.Select("Workspace/Parameters demo");
        main.Details.EditCommand.Execute(null);
        main.Details.Editor!.General.Name = "Renamed";

        main.Select("Workspace/Hello/hello.bat");

        Assert.HasCount(1, confirm.Asked);
        Assert.AreSame(edited, main.SelectedNode);
        Assert.AreEqual("Renamed", main.Details.Editor!.General.Name);

        confirm.Answer = true;
        main.Select("Workspace/Hello/hello.bat");
        Assert.IsNull(main.Details.Editor);
        Assert.AreEqual("hello.bat", main.Details.Node!.Name);
    }

    [TestMethod]
    public void AnUnchangedEditorClosesWithoutAsking()
    {
        using var test = new TestWorkspace();
        var confirm = new FakeConfirm();
        var main = test.OpenMain(test.CopyDemo(), trusted: true, confirm: confirm);
        main.Select("Workspace/Parameters demo");
        main.Details.EditCommand.Execute(null);

        main.Select("Workspace/Hello/hello.bat");
        main.OpenWorkspaceSettingsCommand.Execute(null);
        main.EscapeCommand.Execute(null);

        Assert.IsEmpty(confirm.Asked);
        Assert.IsNull(main.Details.Editor);
        Assert.IsNull(main.WorkspaceSettings);
    }

    [TestMethod]
    public void OpeningAnotherPageOrEscapeAsksAboutChangedWorkspaceSettings()
    {
        using var test = new TestWorkspace();
        var confirm = new FakeConfirm { Answer = false };
        var main = test.OpenMain(test.CopyDemo(), trusted: true, confirm: confirm);
        main.OpenWorkspaceSettingsCommand.Execute(null);
        main.WorkspaceSettings!.Name = "Changed";

        main.OpenInsightsCommand.Execute(null);
        main.EscapeCommand.Execute(null);

        Assert.HasCount(2, confirm.Asked);
        StringAssert.Contains(confirm.Asked[0], "workspace settings");
        Assert.IsNotNull(main.WorkspaceSettings);
        Assert.IsNull(main.Insights);
    }

    [TestMethod]
    public void SwitchingWorkspaceKeepsAnEditTheUserKeeps()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var other = Directory.CreateDirectory(Path.Combine(test.Root, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "batchpad.json"), """{ "id": "other", "name": "Other" }""");
        var confirm = new FakeConfirm { Answer = false };
        var main = test.OpenMain(other, confirm: confirm);
        main.Open(Path.Combine(demo, "batchpad.json"));
        main.Select("Workspace/Parameters demo");
        main.Details.EditCommand.Execute(null);
        main.Details.Editor!.General.Name = "Renamed";

        main.SelectedWorkspace = main.RecentWorkspaces.Single(r => r.DisplayName == "other");

        Assert.AreEqual(Path.Combine(demo, "batchpad.json"), main.Workspace!.FilePath, ignoreCase: true);
        Assert.AreEqual("demo", main.SelectedWorkspace!.DisplayName);
        Assert.IsNotNull(main.Details.Editor);
    }
}
