using BatchPad.App.ViewModels;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class TreeViewModelTests
{
    [TestMethod]
    public void DemoShowsThreeRootsWithItsScripts()
    {
        using var test = new TestWorkspace();
        var tree = test.OpenMain(TestWorkspace.DemoSource).Tree!;

        CollectionAssert.AreEqual(new[] { "My Scripts", "Workspace — Demo", "Global" }, tree.Roots.Select(r => r.Name).ToList());
        Assert.IsEmpty(tree.Roots[0].Children);
        Assert.IsEmpty(tree.Roots[2].Children);

        var workspaceNodes = tree.Roots[1].Descendants().ToList();
        Assert.AreEqual(3, workspaceNodes.Count(n => n.Kind == NodeKind.Folder));
        Assert.AreEqual(11, workspaceNodes.Count(n => n.Kind == NodeKind.Script));
        Assert.IsNotNull(tree.Find("Workspace/Hello/hello.bat"));
    }

    [TestMethod]
    public void FilterKeepsMatchesAndTheirFolders()
    {
        using var test = new TestWorkspace();
        var directory = test.CopyDemo();
        File.WriteAllText(Path.Combine(directory, "batchpad.json"), """
            { "name": "Demo", "scripts": [
              { "folder": "Hello", "items": [ { "path": "hello.bat", "name": "hello.bat" }, { "path": "hello.py", "name": "other.py" } ] },
              { "folder": "Tools", "items": [ { "path": "hello.cs", "name": "tool.cs" } ] } ] }
            """);
        var tree = test.OpenMain(directory).Tree!;

        tree.Filter = "hello";

        var visible = tree.Roots[1].Descendants().Where(n => n.IsVisible).Select(n => n.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "Hello", "hello.bat" }, visible);

        tree.Filter = "tool";
        visible = tree.Roots[1].Descendants().Where(n => n.IsVisible).Select(n => n.Name).ToList();
        CollectionAssert.AreEquivalent(new[] { "Tools", "tool.cs" }, visible);

        tree.Filter = "";
        Assert.IsTrue(tree.AllNodes.All(n => n.IsVisible));
    }

    [TestMethod]
    public void UntrustedWorkspaceIsExposedUntilTrusted()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);

        Assert.IsFalse(main.IsTrusted);
        Assert.IsTrue(main.IsTrustPromptVisible);
        StringAssert.Contains(main.TrustPromptText, "trust");

        main.TrustWorkspaceCommand.Execute(null);

        Assert.IsTrue(main.IsTrusted);
        Assert.IsFalse(main.IsTrustPromptVisible);
        Assert.IsTrue(main.Trust.IsTrusted(TestWorkspace.DemoSource));
    }

    [TestMethod]
    public void SelectingANodeShowsItsDetails()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var node = main.Tree!.Find("Workspace/Hello/hello.py")!;

        node.IsSelected = true;

        Assert.AreSame(node, main.SelectedNode);
        Assert.AreEqual("Workspace › Hello", node.Location);
        Assert.AreEqual("hello.py", node.FilePath);
    }
}
