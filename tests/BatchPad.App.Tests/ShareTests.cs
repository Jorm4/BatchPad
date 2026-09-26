using BatchPad.App.ViewModels;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ShareTests
{
    [TestMethod]
    public void SharingACustomisationMovesItIntoTheWorkspaceWithItsValuesAsDefaults()
    {
        using var test = new TestWorkspace();
        var confirm = new FakeConfirm { Answer = true };
        var main = Open(test, confirm);
        var entry = main.Tree!.MyScriptsRoot.Children.Single();

        main.MyScripts.ShareWithWorkspaceCommand.Execute(entry);

        Assert.HasCount(1, confirm.Asked);
        var shared = ConfigReader.ReadFile(main.Workspace!.FilePath).Scripts.OfType<ScriptNode>().Single(s => s.Name == "Demo release");
        Assert.AreEqual("demo-release", shared.Id);
        Assert.AreEqual("--release", shared.Params!.Single(p => p.Name == "config").Default!.GetValue<string>());
        Assert.IsEmpty(UserStore.For(main.Workspace).Load().Scripts);
        Assert.AreEqual(TreeKind.Workspace, main.Details.Node!.Tree.Kind);
        Assert.AreEqual("Demo release", main.Details.Node.Name);
    }

    [TestMethod]
    public void DecliningTheConfirmationChangesNothing()
    {
        using var test = new TestWorkspace();
        var main = Open(test, new FakeConfirm { Answer = false });

        main.MyScripts.ShareWithWorkspaceCommand.Execute(main.Tree!.MyScriptsRoot.Children.Single());

        Assert.HasCount(1, UserStore.For(main.Workspace!).Load().Scripts);
    }

    [TestMethod]
    public void CopyToMyScriptsRunsTheSameCommand()
    {
        using var test = new TestWorkspace();
        var main = Open(test, new FakeConfirm());
        var original = main.Tree!.AllNodes.Single(n => n.Node is ScriptNode { Id: "hello-bat" });
        original.IsSelected = true;
        var originalCommand = Command(main);

        main.MyScripts.CopyToMyScriptsCommand.Execute(original);

        var copy = main.Details.Node!;
        Assert.IsTrue(copy.IsMyScript);
        Assert.IsNull(((ScriptNode)copy.Node!).Base);
        Assert.AreEqual(originalCommand, Command(main));
    }

    private static string Command(MainViewModel main) =>
        RunPlanner.Plan(main.Details.BuildRequest()!, main.Services.Interpreters).Single().Command.Display;

    private static MainViewModel Open(TestWorkspace test, FakeConfirm confirm)
    {
        var demo = test.CopyDemo();
        var user = test.Paths.UserFile("batchpad-demo");
        Directory.CreateDirectory(Path.GetDirectoryName(user)!);
        File.WriteAllText(user, """{ "scripts": [ { "base": "workspace:params-demo", "name": "Demo release", "values": { "config": "--release" } } ] }""");
        var main = test.OpenMain(demo, confirm: confirm);
        return main;
    }
}
