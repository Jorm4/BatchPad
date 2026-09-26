using BatchPad.App.ViewModels;
using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class OrphanTests
{
    [TestMethod]
    public void DeletedFileOrphansTheEntryAndReattachMovesIt()
    {
        using var test = new TestWorkspace();
        var dialogs = new FakeDialogs();
        var (main, scripts) = Open(test, dialogs);
        File.Delete(Path.Combine(scripts, "a.bat"));
        main.CheckForExternalChanges();
        var orphan = Node(main);
        Assert.IsTrue(orphan.IsOrphan);

        File.WriteAllText(Path.Combine(scripts, "c.bat"), "@echo c");
        dialogs.File = Path.Combine(scripts, "c.bat");
        orphan.IsSelected = true;
        main.Details.ReattachCommand.Execute(null);

        var entry = Entry(main);
        Assert.AreEqual(".batchpad/scripts/c.bat", entry.Path);
        Assert.AreEqual("mode", entry.Params!.Single().Name);
        Assert.IsFalse(main.Details.Node!.IsOrphan);
        Assert.AreEqual("tool-a", ((ScriptNode)main.Details.Node.Node!).Id);
    }

    [TestMethod]
    public void RenameSeenWhileOpenMovesTheEntry()
    {
        using var test = new TestWorkspace();
        var (main, scripts) = Open(test, new FakeDialogs());
        File.Move(Path.Combine(scripts, "a.bat"), Path.Combine(scripts, "b.bat"));
        main.Renames.Observe(new FileChange(WatcherChangeTypes.Renamed, Path.Combine(scripts, "b.bat"), Path.Combine(scripts, "a.bat")));
        main.CheckForExternalChanges();

        Assert.AreEqual(".batchpad/scripts/b.bat", Entry(main).Path);
        Assert.IsFalse(Node(main).IsOrphan);
    }

    private static (MainViewModel, string) Open(TestWorkspace test, FakeDialogs dialogs)
    {
        var demo = test.CopyDemo();
        var scripts = Directory.CreateDirectory(Path.Combine(demo, ".batchpad", "scripts")).FullName;
        File.WriteAllText(Path.Combine(scripts, "a.bat"), "@echo a");
        ConfigWriter.Update(Path.Combine(demo, "batchpad.json"), file => file.Scripts.Add(new ScriptNode
        {
            Id = "tool-a", Path = ".batchpad/scripts/a.bat", Params = [new ParameterDefinition { Name = "mode", Type = ParameterType.Text }],
        }));
        var main = test.OpenMain(demo, dialogs: dialogs);
        return (main, scripts);
    }

    private static NodeViewModel Node(MainViewModel main) =>
        main.Tree!.AllNodes.Single(n => n.Node is ScriptNode { Id: "tool-a" });

    private static ScriptNode Entry(MainViewModel main) =>
        ConfigReader.ReadFile(main.Workspace!.FilePath).Scripts.OfType<ScriptNode>().Single(s => s.Id == "tool-a");
}

