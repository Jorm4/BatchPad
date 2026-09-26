using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class MyScriptsTests
{
    private const string BuildAndRun = "Workspace/Build & run";

    [TestMethod]
    public void DroppingASharedScriptCreatesACustomisationNamedByItsTemplate()
    {
        using var test = new TestWorkspace();
        var main = OpenBuildWorkspace(test);
        var source = main.Select(BuildAndRun);
        Choose(main, "config", "Release");
        Choose(main, "app", "SpaceTrader");

        Assert.IsTrue(main.MyScripts.DragDrop.Drop(source, main.Tree!.MyScriptsRoot));

        var entry = UserEntries(main).Single();
        Assert.AreEqual("Build Release & run SpaceTrader", entry.Name);
        Assert.AreEqual("workspace:build-run", entry.Base);
        Assert.AreEqual("SpaceTrader", entry.Values!["app"]!.GetValue<string>());
        var node = main.SelectedNode!;
        Assert.AreEqual("MyScripts/Build Release & run SpaceTrader", node.AutomationId);
        StringAssert.Contains(main.Details.CustomisesText, "Workspace › Build & run");
        StringAssert.Contains(main.Details.Preview, "SpaceTrader");
    }

    [TestMethod]
    public void DuplicateThenChangingTheAppGivesTwoEntries()
    {
        using var test = new TestWorkspace();
        var main = OpenBuildWorkspace(test);
        main.Select(BuildAndRun);
        Choose(main, "config", "Release");
        Choose(main, "app", "SpaceTrader");
        main.Details.SaveAsMyScriptCommand.Execute(null);

        main.Details.DuplicateCommand.Execute(null);
        Choose(main, "app", "RallyRacer");
        main.Details.SaveCommand.Execute(null);

        CollectionAssert.AreEquivalent(
            new[] { "Build Release & run SpaceTrader", "Build Release & run RallyRacer" },
            UserEntries(main).Select(e => e.Name).ToList());
        Assert.AreEqual(2, UserEntries(main).Select(e => e.Id).Distinct().Count());
    }

    [TestMethod]
    public void ADuplicateHasItsOwnIdAndKeepsItsOwnValues()
    {
        using var test = new TestWorkspace();
        var main = OpenBuildWorkspace(test);
        main.Select(BuildAndRun);
        Choose(main, "app", "SpaceTrader");
        main.Details.SaveAsMyScriptCommand.Execute(null);
        var original = main.SelectedNode!.Key;

        main.Details.DuplicateCommand.Execute(null);
        var copy = main.SelectedNode!;
        Assert.AreNotEqual(original, copy.Key);
        Choose(main, "app", "RallyRacer");

        main.Tree!.AllNodes.Single(n => n.Key == original).IsSelected = true;
        Assert.AreEqual("SpaceTrader", ((ChoiceFieldViewModel)main.Details.Form!.Field("app")!).Selected!.Label);
        main.Tree.AllNodes.Single(n => n.Key == copy.Key).IsSelected = true;
        Assert.AreEqual("RallyRacer", ((ChoiceFieldViewModel)main.Details.Form!.Field("app")!).Selected!.Label);
    }

    [TestMethod]
    public void AFolderCannotBeDroppedOntoItsOwnChild()
    {
        using var test = new TestWorkspace();
        var main = OpenBuildWorkspace(test);
        UserStore.For(main.Workspace!).Update(file => file.Scripts.Add(new FolderNode
        {
            Folder = "Daily",
            Items = [new FolderNode { Folder = "Inner" }],
        }));
        main.Reload();
        var before = File.ReadAllText(main.Workspace!.MyScripts.FilePath);
        var daily = main.Tree!.Find("MyScripts/Daily")!;
        var inner = main.Tree.Find("MyScripts/Daily/Inner")!;

        Assert.IsFalse(DragDropHandler.CanDrop(daily, inner));
        Assert.IsFalse(main.MyScripts.DragDrop.Drop(daily, inner));
        Assert.AreEqual(before, File.ReadAllText(main.Workspace.MyScripts.FilePath));

        Assert.IsTrue(main.MyScripts.DragDrop.Drop(inner, main.Tree.MyScriptsRoot));
        Assert.IsNotNull(main.Tree.Find("MyScripts/Inner"));
    }

    [TestMethod]
    public void DeletingTheBaseShowsTheEntryBrokenUntilItIsRepointed()
    {
        using var test = new TestWorkspace();
        var main = OpenBuildWorkspace(test);
        main.MyScripts.AddToMyScriptsCommand.Execute(main.Select("Workspace/Other"));

        File.WriteAllText(Path.Combine(main.Workspace!.Directory, "batchpad.json"), WorkspaceJson(includeOther: false));
        main.Reload();

        var entry = main.Tree!.Find("MyScripts/Other")!;
        Assert.IsTrue(entry.IsBroken);
        entry.IsSelected = true;
        Assert.IsFalse(main.Details.IsRunnable);
        Assert.IsTrue(main.Details.OpenChangeBaseCommand.CanExecute(null));

        main.Details.OpenChangeBaseCommand.Execute(null);
        var picker = main.Details.ChangeBase!;
        picker.Selected = picker.Choices.Single(c => c.Reference == "workspace:build-run");
        picker.ApplyCommand.Execute(null);

        Assert.IsFalse(main.Tree.Find("MyScripts/Other")!.IsBroken);
    }

    [TestMethod]
    public void RenameAndDeleteEditUserJson()
    {
        using var test = new TestWorkspace();
        var confirm = new FakeConfirm { Answer = true };
        var main = OpenBuildWorkspace(test, confirm);
        main.MyScripts.AddToMyScriptsCommand.Execute(main.Select("Workspace/Other"));
        var node = main.Tree!.Find("MyScripts/Other")!;

        main.MyScripts.BeginRenameCommand.Execute(node);
        node.RenameText = "Mine";
        main.MyScripts.CommitRenameCommand.Execute(node);
        Assert.AreEqual("Mine", UserEntries(main).Single().Name);

        main.MyScripts.DeleteCommand.Execute(main.Tree.Find("MyScripts/Mine"));
        Assert.HasCount(1, confirm.Asked);
        Assert.IsEmpty(UserEntries(main));
    }

    [TestMethod]
    public void PinningAWebArtifactKeepsItsUrl()
    {
        using var test = new TestWorkspace();
        var main = OpenBuildWorkspace(test);

        Assert.IsTrue(main.MyScripts.PinLink("https://example.com/report"));

        var link = UserStore.For(main.Workspace!).Load().Scripts.OfType<LinkNode>().Single();
        Assert.AreEqual("https://example.com/report", link.Url);
        Assert.AreEqual("example.com", link.Name);
    }

    private static MainViewModel OpenBuildWorkspace(TestWorkspace test, FakeConfirm? confirm = null)
    {
        var directory = Directory.CreateDirectory(Path.Combine(test.Root, "build")).FullName;
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), WorkspaceJson(includeOther: true));
        File.WriteAllText(Path.Combine(directory, "build_run.bat"), "@echo %*\r\n");
        var main = test.OpenMain(directory, trusted: true, launcher: new FakeLauncher(), shell: new FakeShell(), confirm: confirm ?? new FakeConfirm());
        return main;
    }

    private static string WorkspaceJson(bool includeOther) => $$"""
        {
          "id": "build-demo",
          "scripts": [
            { "id": "build-run", "name": "Build & run", "path": "build_run.bat",
              "nameTemplate": "Build ${param:config.label} & run ${param:app}",
              "params": [
                { "name": "config", "type": "choice", "default": "", "choices": [
                    { "value": "", "label": "Debug" }, { "value": "--release", "label": "Release" } ] },
                { "name": "app", "type": "choice", "choices": ["SpaceTrader", "RallyRacer"] }
              ] }{{(includeOther ? """
            ,
            { "id": "other", "name": "Other", "path": "build_run.bat" }
            """ : "")}}
          ]
        }
        """;

    private static void Choose(MainViewModel main, string parameter, string label)
    {
        var field = (ChoiceFieldViewModel)main.Details.Form!.Field(parameter)!;
        field.Selected = field.Options.Single(o => o.Label == label);
    }

    private static List<ScriptNode> UserEntries(MainViewModel main) =>
        UserStore.For(main.Workspace!).Load().Scripts.OfType<ScriptNode>().ToList();
}
