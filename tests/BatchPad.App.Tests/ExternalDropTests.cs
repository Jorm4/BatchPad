using BatchPad.App.ViewModels;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ExternalDropTests
{
    [TestMethod]
    public void CopyPutsTheFileIntoTheScriptFolder()
    {
        using var test = new TestWorkspace();
        var (main, dropped) = Open(test);
        var handler = main.MyScripts.DragDrop;
        handler.DropExternal([dropped], main.Tree!.Roots[1]);
        Assert.AreEqual("Add tool.bat to Workspace:", handler.PendingDrop!.Message);

        handler.PendingDrop.CopyCommand.Execute(null);

        var copy = Path.Combine(main.Workspace!.Directory, ".batchpad", "scripts", "tool.bat");
        Assert.IsTrue(File.Exists(copy));
        Assert.IsNull(handler.PendingDrop);
        Assert.AreEqual(copy, main.Details.Node!.ScriptFullPath, ignoreCase: true);
        Assert.IsFalse(ConfigReader.ReadFile(main.Workspace.FilePath).Scripts.OfType<ScriptNode>().Any(s => s.Path?.EndsWith("tool.bat") == true));
    }

    [TestMethod]
    public void ReferenceAddsAPathEntry()
    {
        using var test = new TestWorkspace();
        var (main, dropped) = Open(test);
        main.MyScripts.DragDrop.DropExternal([dropped], main.Tree!.Roots[1]);
        main.MyScripts.DragDrop.PendingDrop!.ReferenceCommand.Execute(null);

        var entry = ConfigReader.ReadFile(main.Workspace!.FilePath).Scripts.OfType<ScriptNode>().Single(s => s.Id == "tool");
        Assert.AreEqual("../outside/tool.bat", entry.Path);
        Assert.AreEqual("tool", ((ScriptNode)main.Details.Node!.Node!).Id);
    }

    [TestMethod]
    public void UrlOntoMyScriptsAddsALink()
    {
        using var test = new TestWorkspace();
        var (main, _) = Open(test);
        main.MyScripts.DragDrop.DropExternal(["https://example.com"], main.Tree!.MyScriptsRoot);

        var link = main.Tree!.MyScriptsRoot.Children.Single(n => n.Kind == NodeKind.Link);
        Assert.AreEqual("example.com", link.Name);
        Assert.AreEqual("https://example.com", ((LinkNode)link.Node!).Url);
    }

    [TestMethod]
    public void ScriptOntoMyScriptsMakesAStandaloneEntry()
    {
        using var test = new TestWorkspace();
        var (main, dropped) = Open(test);
        main.MyScripts.DragDrop.DropExternal([dropped], main.Tree!.MyScriptsRoot);

        var entry = (ScriptNode)main.Tree!.MyScriptsRoot.Children.Single().Node!;
        Assert.AreEqual(dropped, entry.Path, ignoreCase: true);
        Assert.IsNull(entry.Base);
    }

    private static (MainViewModel, string) Open(TestWorkspace test)
    {
        var demo = test.CopyDemo();
        var outside = Directory.CreateDirectory(Path.Combine(test.Root, "outside")).FullName;
        var dropped = Path.Combine(outside, "tool.bat");
        File.WriteAllText(dropped, "@echo tool");
        return (test.OpenMain(demo), dropped);
    }
}
