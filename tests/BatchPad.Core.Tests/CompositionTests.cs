using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class CompositionTests
{
    [TestMethod]
    public void AnIncludedScriptShowsUnderItsFolderAndRunsFromItsOwnFile()
    {
        using var dir = new TempDir();
        var loaded = LoadWithInclude(dir);

        Assert.IsEmpty(loaded.Errors);
        var folder = loaded.Workspace.Items.Single(i => i.Part is not null);
        Assert.AreEqual("Tools", folder.Name);
        Assert.AreEqual("Regenerate", folder.Children.Single().Children.Single().Name);

        var regen = (ScriptNode)loaded.References.Resolve("workspace:regen", TreeKind.Workspace)!;
        var tree = loaded.References.TreeOf(regen)!;
        Assert.AreSame(folder.Part, tree);
        Assert.AreEqual(TreeKind.Workspace, tree.Kind);

        var command = RunPlanner.Plan(new RunRequest(loaded, tree, regen), new InterpreterLocator()).Single().Command;
        StringAssert.Contains(command.Display, dir.Path("tools", "regen.bat"));
        Assert.AreEqual("workspace:build", Prerequisites.ChainOf(regen, tree, loaded.References).Single().Reference);
    }

    [TestMethod]
    public void ADuplicateIdAcrossFilesIsAnErrorNamingBoth()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path("tools"));
        File.WriteAllText(dir.Path("batchpad.json"), File.ReadAllText(Fixtures.Path("config", "with_include.json")));
        File.WriteAllText(dir.Path("tools", "part.json"), """{ "scripts": [ { "id": "build", "command": "echo other" } ] }""");

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), new AppPaths(dir.Path("data"), isPortable: false));

        var error = loaded.Errors.Single();
        StringAssert.Contains(error.Message, "'build'");
        StringAssert.Contains(error.Message, "batchpad.json");
        StringAssert.Contains(error.Message, Path.Combine("tools", "part.json"));
    }

    [TestMethod]
    public void ALibraryScriptResolvesByLibraryName()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path("libs"));
        File.Copy(Fixtures.Path("config", "lib.json"), dir.Path("libs", "lib.json"));
        var paths = new AppPaths(dir.Path("data"), isPortable: false);
        ConfigWriter.Write(paths.GlobalFile, new WorkspaceFile { Libraries = [dir.Path("libs", "lib.json")] });
        File.WriteAllText(dir.Path("batchpad.json"), "{}");

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), paths);

        Assert.IsEmpty(loaded.Errors);
        var tool = loaded.References.Resolve("global:lib:tool", TreeKind.Workspace)!;
        Assert.AreEqual("Team tool", ScriptTree.DisplayName(tool));
        Assert.IsNull(loaded.References.Resolve("global:tool", TreeKind.Workspace));
        var library = loaded.References.TreeOf(tool)!;
        Assert.AreEqual(dir.Path("libs"), library.BaseDirectory);
        Assert.AreSame(tool, loaded.References.Resolve("tool", library));
        Assert.AreEqual("Team library", loaded.Global.Items.Single(i => i.Part is not null).Name);
        Assert.AreEqual("global:lib:tool", loaded.References.QualifiedReference(tool));
    }

    [TestMethod]
    public void AMissingIncludeIsReported()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), File.ReadAllText(Fixtures.Path("config", "with_include.json")));

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), new AppPaths(dir.Path("data"), isPortable: false));

        Assert.AreEqual(dir.Path("tools", "part.json"), loaded.Errors.Single().FilePath);
        Assert.IsNotNull(loaded.References.Resolve("build", TreeKind.Workspace));
    }

    private static LoadedWorkspace LoadWithInclude(TempDir dir)
    {
        Directory.CreateDirectory(dir.Path("tools"));
        File.Copy(Fixtures.Path("config", "with_include.json"), dir.Path("batchpad.json"));
        File.Copy(Fixtures.Path("config", "part.json"), dir.Path("tools", "part.json"));
        File.WriteAllText(dir.Path("tools", "regen.bat"), "@echo regen");
        return WorkspaceLoader.Load(dir.Path("batchpad.json"), new AppPaths(dir.Path("data"), isPortable: false));
    }
}
