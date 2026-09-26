using BatchPad.App.ViewModels;
using BatchPad.Core.Config;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class MainViewModelTests
{
    [TestMethod]
    public void OpeningASecondWorkspacePutsItAtTheTopOfTheRecents()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var other = Directory.CreateDirectory(Path.Combine(test.Root, "other")).FullName;
        File.WriteAllText(Path.Combine(other, "batchpad.json"), """{ "id": "other", "name": "Other" }""");
        var main = test.OpenMain(demo);

        main.Open(Path.Combine(other, "batchpad.json"));

        CollectionAssert.AreEqual(new[] { "other", "demo" }, main.RecentWorkspaces.Select(r => r.DisplayName).ToList());
        Assert.AreEqual("other", main.SelectedWorkspace!.DisplayName);

        main.SelectedWorkspace = main.RecentWorkspaces[1];

        Assert.AreEqual(Path.Combine(demo, "batchpad.json"), main.Workspace!.FilePath, ignoreCase: true);
        Assert.AreEqual("demo", main.RecentWorkspaces[0].DisplayName);
    }

    [TestMethod]
    public void AnExternalEditReloadsOrPromptsWhileEditing()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var file = Path.Combine(demo, "batchpad.json");
        var main = test.OpenMain(demo, trusted: true);

        main.CheckForExternalChanges();
        Assert.IsFalse(main.IsReloadPromptVisible);

        ConfigWriter.Update(file, f => f.Name = "Renamed outside");
        main.CheckForExternalChanges();
        Assert.IsFalse(main.IsReloadPromptVisible);
        Assert.AreEqual("Workspace — Renamed outside", main.Tree!.Roots[1].Name);

        main.Tree.Find("Workspace/Hello/hello.bat")!.IsSelected = true;
        main.Details.EditCommand.Execute(null);
        ConfigWriter.Update(file, f => f.Name = "Renamed again");
        main.CheckForExternalChanges();
        Assert.IsTrue(main.IsReloadPromptVisible);
        Assert.IsNotNull(main.Details.Editor);

        main.ReloadFromDiskCommand.Execute(null);
        Assert.IsFalse(main.IsReloadPromptVisible);
        Assert.IsNull(main.Details.Editor);
        Assert.AreEqual("Workspace — Renamed again", main.Tree!.Roots[1].Name);
        Assert.AreEqual("Workspace/Hello/hello.bat", main.SelectedNode!.AutomationId);
    }

    [TestMethod]
    public void TheAppsOwnSaveDoesNotPrompt()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(test.CopyDemo(), trusted: true);
        main.Tree!.Find("Workspace/Hello/hello.bat")!.IsSelected = true;
        main.Details.EditCommand.Execute(null);
        main.Details.Editor!.General.Description = "Says hello.";
        main.Details.Editor.SaveCommand.Execute(null);

        main.CheckForExternalChanges();

        Assert.IsFalse(main.IsReloadPromptVisible);
    }

    [TestMethod]
    public void TheFilterMatchesADescriptionWord()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(test.CopyDemo());

        main.Tree!.Filter = "arguments";

        Assert.IsTrue(main.Tree.Find("Workspace/Parameters demo")!.IsVisible);
        Assert.IsFalse(main.Tree.Find("Workspace/Hello/hello.bat")!.IsVisible);
    }

    [TestMethod]
    public void SelectingANewScriptClearsItsBadgeForGood()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var main = test.OpenMain(demo);
        var scripts = Directory.CreateDirectory(Path.Combine(demo, ".batchpad", "scripts")).FullName;
        File.WriteAllText(Path.Combine(scripts, "fresh.bat"), "@echo fresh\r\n");
        main.Reload();

        var fresh = main.Tree!.Find("Workspace/Fresh")!;
        Assert.IsTrue(fresh.IsNew);
        fresh.IsSelected = true;
        Assert.IsFalse(fresh.IsNew);

        main.Reload();
        Assert.IsFalse(main.Tree!.Find("Workspace/Fresh")!.IsNew);
    }

    [TestMethod]
    public async Task ARunningScriptKeepsItsBadgeAndStopAfterAnEditorSave()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = new MainViewModel(test.Paths, new Settings(), launcher, shell: new FakeShell());
        var demo = test.CopyDemo();
        main.Trust.Trust(demo);
        main.Open(Path.Combine(demo, "batchpad.json"));
        main.Tree!.Find("Workspace/Hello/hello.bat")!.IsSelected = true;
        main.Details.RunCommand.Execute(null);

        main.Details.EditCommand.Execute(null);
        main.Details.Editor!.General.Description = "Says hello.";
        main.Details.Editor.SaveCommand.Execute(null);

        var node = main.SelectedNode!;
        Assert.AreEqual("Says hello.", node.Description);
        Assert.AreEqual(RunBadge.Running, node.Badge);
        Assert.IsTrue(main.Details.StopCommand.CanExecute(null));
        Assert.AreSame(node, main.Output.Tabs.Single().Node);

        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
        await main.Output.Tabs[0].Finished;
        Assert.AreEqual(RunBadge.Passed, node.Badge);
        Assert.IsFalse(main.Details.StopCommand.CanExecute(null));
    }
}
