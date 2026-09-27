using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ScriptEditorTests
{
    private const string ToolScript = """
        @echo off
        rem Builds the tool.
        rem
        rem Usage:
        rem   tool_one.bat [--release] [--clean]
        echo %*
        """;

    [TestMethod]
    public void RenamingADiscoveredScriptCreatesAnEntryWithPathAndName()
    {
        using var test = new TestWorkspace();
        var (main, demo) = OpenWithDiscoveredTool(test);
        var editor = Edit(main, Discovered(main));

        editor.General.Name = "Tool one (renamed)";
        editor.SaveCommand.Execute(null);

        var entry = ConfigReader.ReadFile(Path.Combine(demo, "batchpad.json")).Scripts.OfType<ScriptNode>()
            .Single(s => s.Path == ".batchpad/scripts/tool_one.bat");
        Assert.AreEqual("Tool one (renamed)", entry.Name);
        Assert.AreEqual("tool-one", entry.Id);
        Assert.IsNull(main.Details.Editor);
        Assert.AreEqual("Tool one (renamed)", main.SelectedNode!.Name);
    }

    [TestMethod]
    public void AcceptingAProposedFlagAddsItToParams()
    {
        using var test = new TestWorkspace();
        var (main, demo) = OpenWithDiscoveredTool(test);
        var editor = Edit(main, Discovered(main));
        var release = editor.Parameters.Proposals.Single(p => p.Parameter?.Arg == "--release");
        Assert.AreEqual(ParameterType.Flag, release.Parameter!.Type);

        release.AcceptCommand.Execute(null);
        Assert.AreSame(release.Parameter, editor.Parameters.Items.Single().Definition);
        Assert.DoesNotContain(release, editor.Parameters.Proposals);
        editor.Parameters.Items.Single().DefaultText = "true";
        Assert.Contains("--release", editor.Preview);
        editor.SaveCommand.Execute(null);

        var saved = ReadTool(demo);
        Assert.AreEqual("--release", saved.Params!.Single().Arg);
    }

    [TestMethod]
    public void DismissingAProposalDoesNotAddIt()
    {
        using var test = new TestWorkspace();
        var (main, demo) = OpenWithDiscoveredTool(test);
        var editor = Edit(main, Discovered(main));

        editor.Parameters.Proposals.Single(p => p.Parameter?.Arg == "--clean").DismissCommand.Execute(null);
        editor.General.Description = "Builds it.";
        editor.SaveCommand.Execute(null);

        Assert.IsFalse(editor.Parameters.Proposals.Any(p => p.Parameter?.Arg == "--clean"));
        Assert.IsEmpty(editor.Parameters.Items);
        var saved = ReadTool(demo);
        Assert.AreEqual("Builds it.", saved.Description);
        Assert.IsNull(saved.Params);
    }

    [TestMethod]
    public void CancelLeavesTheFileByteIdentical()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var file = Path.Combine(demo, "batchpad.json");
        var before = File.ReadAllBytes(file);
        var main = test.OpenMain(demo, trusted: true);
        var editor = Edit(main, main.Tree!.Find("Workspace/Parameters demo")!);

        editor.General.Name = "Changed";
        editor.Parameters.AddCommand.Execute(null);
        editor.Parameters.Field("port")!.DefaultText = "1";
        editor.CancelCommand.Execute(null);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(file));
        Assert.IsNull(main.Details.Editor);
        Assert.Contains("--port 8123", main.Details.Preview);
    }

    [TestMethod]
    public void EditClosesAnOpenPageSoTheEditorShows()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(test.CopyDemo(), trusted: true);
        main.Tree!.Find("Workspace/Parameters demo")!.IsSelected = true;
        main.OpenSettingsCommand.Execute(null);

        main.Details.EditCommand.Execute(null);

        Assert.IsFalse(main.IsPageOpen);
        Assert.IsNotNull(main.Details.Editor);
    }

    [TestMethod]
    public void TheSharedBannerShowsForWorkspaceScriptsButNotMyScripts()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var userFile = test.Paths.UserFile("batchpad-demo");
        ConfigWriter.Update(userFile, f => f.Scripts.Add(new ScriptNode { Id = "mine", Name = "Mine", Path = "mine.bat" }));
        var main = test.OpenMain(demo, trusted: true);

        Assert.IsTrue(Edit(main, main.Tree!.Find("Workspace/Hello/hello.bat")!).IsShared);
        Assert.IsFalse(Edit(main, main.Tree!.Find("MyScripts/Mine")!).IsShared);
    }

    [TestMethod]
    public void TestRunRunsTheUnsavedDefinition()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var launcher = new FakeLauncher();
        var main = test.OpenMain(demo, trusted: true, launcher: launcher, shell: new FakeShell());
        var editor = Edit(main, main.Tree!.Find("Workspace/Parameters demo")!);

        editor.Parameters.Field("port")!.DefaultText = "9999";
        editor.TestRunCommand.Execute(null);

        Assert.AreEqual(9999, (int)launcher.Requests.Single().Script.Params!.Single(p => p.Name == "port").Default!);
        Assert.HasCount(1, main.Output.Tabs);
        Assert.Contains("\"default\": 8123", File.ReadAllText(Path.Combine(demo, "batchpad.json")));
    }

    [TestMethod]
    public void SavingAnIncludedEntryChangesOnlyItsOwnFile()
    {
        using var test = new TestWorkspace();
        var root = Directory.CreateDirectory(Path.Combine(test.Root, "composed", "tools")).Parent!.FullName;
        var fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures", "config");
        File.Copy(Path.Combine(fixtures, "with_include.json"), Path.Combine(root, "batchpad.json"));
        File.Copy(Path.Combine(fixtures, "part.json"), Path.Combine(root, "tools", "part.json"));
        File.WriteAllText(Path.Combine(root, "tools", "regen.bat"), "@echo regen");
        var workspaceBefore = File.ReadAllText(Path.Combine(root, "batchpad.json"));
        var main = test.OpenMain(root, trusted: true);

        var editor = Edit(main, main.Tree!.AllNodes.Single(n => n.Name == "Regenerate"));
        editor.General.Name = "Regenerate all";
        editor.SaveCommand.Execute(null);

        Assert.AreEqual(workspaceBefore, File.ReadAllText(Path.Combine(root, "batchpad.json")));
        var part = ConfigReader.ReadFile(Path.Combine(root, "tools", "part.json"));
        Assert.AreEqual("Regenerate all", ((ScriptNode)((FolderNode)part.Scripts.Single()).Items.Single()).Name);
        Assert.AreEqual("Regenerate all", main.SelectedNode!.Name);
    }

    private static (MainViewModel, string) OpenWithDiscoveredTool(TestWorkspace test)
    {
        var demo = test.CopyDemo();
        var scripts = Directory.CreateDirectory(Path.Combine(demo, ".batchpad", "scripts")).FullName;
        File.WriteAllText(Path.Combine(scripts, "tool_one.bat"), ToolScript.ReplaceLineEndings("\r\n"));
        return (test.OpenMain(demo, trusted: true), demo);
    }

    private static NodeViewModel Discovered(MainViewModel main) =>
        main.Tree!.AllNodes.Single(n => n.Item is { IsDiscovered: true, HasEntry: false });

    private static ScriptNode ReadTool(string demo) =>
        ConfigReader.ReadFile(Path.Combine(demo, "batchpad.json")).Scripts.OfType<ScriptNode>()
            .Single(s => s.Path == ".batchpad/scripts/tool_one.bat");

    private static ScriptEditorViewModel Edit(MainViewModel main, NodeViewModel node)
    {
        node.IsSelected = true;
        main.Details.EditCommand.Execute(null);
        return main.Details.Editor!;
    }
}
