using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Workspace;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class NewScriptTests
{
    [TestMethod]
    public void NewPythonScriptIsWrittenFromTheTemplateDiscoveredAndOpened()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var shell = new FakeShell();
        var main = new MainViewModel(test.Paths, new Settings { EditorCommand = "edit {file}" }, shell: shell);
        main.OpenInitial(demo, test.Root);

        main.Tree!.NewScriptCommand.Execute(null);
        var item = main.NewItem!;
        Assert.AreEqual(".batchpad/scripts", item.ScriptFolder);
        item.Name = "make report";
        item.ScriptType = ScriptTemplates.All.Single(t => t.Extension == ".py");
        item.SaveCommand.Execute(null);

        var path = Path.Combine(demo, ".batchpad", "scripts", "make_report.py");
        StringAssert.Contains(File.ReadAllText(path), "\"\"\"Make report.\"\"\"");
        var node = main.Details.Node!;
        Assert.AreEqual(path, node.ScriptFullPath, ignoreCase: true);
        Assert.IsTrue(node.Item!.IsDiscovered);
        Assert.AreEqual($"edit {path}", shell.Commands.Single());
    }

    [TestMethod]
    public void EveryTemplateIsEmbedded()
    {
        foreach (var template in ScriptTemplates.All)
            StringAssert.Contains(ScriptTemplates.Content(template.Extension, "tool" + template.Extension), "Tool");
    }
}
