using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class WorkspaceLoaderTests
{
    private static string DemoFile => Path.Combine(Fixtures.DemoWorkspace, "batchpad.json");

    [TestMethod]
    public void LoadsDemoIntoThreeTrees()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root, isPortable: false);
        ConfigWriter.Write(paths.GlobalFile, new WorkspaceFile
        {
            Scripts = [new ScriptNode { Id = "clean-temp", Name = "Clean temp", Command = "echo clean" }],
        });
        ConfigWriter.Write(paths.UserFile("batchpad-demo"), new WorkspaceFile
        {
            Scripts = [new FolderNode { Folder = "Daily", Items = [new ScriptNode { Base = "workspace:hello-py", Name = "Hi" }] }],
        });

        var loaded = WorkspaceLoader.Load(DemoFile, paths);

        Assert.IsEmpty(loaded.Errors);
        Assert.AreEqual("batchpad-demo", loaded.Id);
        Assert.AreEqual(Fixtures.DemoWorkspace, loaded.Directory);
        Assert.AreEqual(10, loaded.Workspace.AllNodes().Count());
        Assert.AreEqual(1, loaded.Global.AllNodes().Count());
        Assert.AreEqual(2, loaded.MyScripts.AllNodes().Count());
        Assert.HasCount(4, loaded.Workspace.Items[0].Children);

        var customisation = (ScriptNode)loaded.MyScripts.AllNodes().Last().Node;
        var baseNode = loaded.References.Resolve(customisation.Base!, TreeKind.MyScripts);
        Assert.AreEqual("hello.py", ((ScriptNode)baseNode!).Name);
        Assert.AreEqual("Clean temp", ((ScriptNode)loaded.References.Resolve("global:clean-temp", TreeKind.Workspace)!).Name);
        Assert.IsNotNull(loaded.References.Resolve("hello-bat", TreeKind.Workspace));
        Assert.IsNull(loaded.References.Resolve("hello-bat", TreeKind.Global));
    }

    [TestMethod]
    public void MissingGlobalAndUserFilesGiveEmptyTrees()
    {
        using var dir = new TempDir();
        var loaded = WorkspaceLoader.Load(DemoFile, new AppPaths(dir.Root, isPortable: false));

        Assert.IsEmpty(loaded.Errors);
        Assert.IsEmpty(loaded.Global.File.Scripts);
        Assert.AreEqual(dir.Path("workspaces", "batchpad-demo", "user.json"), loaded.MyScripts.FilePath);
    }

    [TestMethod]
    public void DuplicateIdIsAnErrorNamingBothNodes()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), """
            { "scripts": [
                { "folder": "Build", "items": [ { "id": "build", "name": "Build game", "path": "a.bat" } ] },
                { "id": "build", "name": "Build tools", "path": "b.bat" }
            ] }
            """);

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), new AppPaths(dir.Path("data"), isPortable: false));

        var error = loaded.Errors.Single();
        StringAssert.Contains(error.Message, "'build'");
        StringAssert.Contains(error.Message, "Build › Build game");
        StringAssert.Contains(error.Message, "Build tools");
        Assert.AreEqual(dir.Path("batchpad.json"), error.FilePath);
    }

    [TestMethod]
    public void BrokenFileIsReportedAndLoadsEmpty()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), "{ \"scripts\": [ ");

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), new AppPaths(dir.Path("data"), isPortable: false));

        Assert.AreEqual(dir.Path("batchpad.json"), loaded.Errors.Single().FilePath);
        Assert.IsEmpty(loaded.Workspace.File.Scripts);
    }

    [TestMethod]
    public void IdFallsBackToPathHash()
    {
        var a = WorkspaceLoader.ComputeId(new WorkspaceFile(), @"C:\repo\batchpad.json");
        var b = WorkspaceLoader.ComputeId(new WorkspaceFile(), @"c:\REPO\batchpad.json");
        var other = WorkspaceLoader.ComputeId(new WorkspaceFile(), @"C:\other\batchpad.json");

        Assert.AreEqual(a, b);
        Assert.AreNotEqual(a, other);
        Assert.AreEqual(16, a.Length);
        Assert.AreNotEqual("..", WorkspaceLoader.ComputeId(new WorkspaceFile { Id = ".." }, @"C:\repo\batchpad.json"));
    }
}
