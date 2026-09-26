using BatchPad.App.ViewModels;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ReloadMergeTests
{
    [TestMethod]
    public void ChangeToAnotherEntryReloadsSilentlyAndTheEditSavesWithBoth()
    {
        using var test = new TestWorkspace();
        var (main, file) = OpenEditing(test);
        var editor = main.Details.Editor!;
        editor.General.Description = "Edited in the app";

        ConfigWriter.Update(file, f => HelloBat(f).Description = "Edited outside");
        main.CheckForExternalChanges();

        Assert.IsFalse(main.IsReloadPromptVisible);
        Assert.AreSame(editor, main.Details.Editor);
        Assert.AreEqual("Edited outside", main.Tree!.AllNodes.Single(n => n.Node is ScriptNode { Id: "hello-bat" }).Description);

        editor.SaveCommand.Execute(null);

        var saved = ConfigReader.ReadFile(file);
        Assert.AreEqual("Edited in the app", saved.Scripts.OfType<ScriptNode>().Single(s => s.Id == "params-demo").Description);
        Assert.AreEqual("Edited outside", HelloBat(saved).Description);
        Assert.IsNull(main.Details.Editor);
    }

    [TestMethod]
    public void ChangeToTheEditedEntryPrompts()
    {
        using var test = new TestWorkspace();
        var (main, file) = OpenEditing(test);
        var editor = main.Details.Editor!;

        ConfigWriter.Update(file, f => f.Scripts.OfType<ScriptNode>().Single(s => s.Id == "params-demo").Description = "Edited outside");
        main.CheckForExternalChanges();

        Assert.IsTrue(main.IsReloadPromptVisible);
        Assert.AreSame(editor, main.Details.Editor);
    }

    private static (MainViewModel, string) OpenEditing(TestWorkspace test)
    {
        var demo = test.CopyDemo();
        var main = test.OpenMain(demo, trusted: true);
        main.Tree!.AllNodes.Single(n => n.Node is ScriptNode { Id: "params-demo" }).IsSelected = true;
        main.Details.EditCommand.Execute(null);
        return (main, Path.Combine(demo, "batchpad.json"));
    }

    private static ScriptNode HelloBat(WorkspaceFile file) =>
        file.Scripts.OfType<FolderNode>().SelectMany(f => f.Items).OfType<ScriptNode>().Single(s => s.Id == "hello-bat");
}
