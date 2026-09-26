using BatchPad.Core.Customisation;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class FlattenerTests
{
    [TestMethod]
    public void CustomisationBecomesAnEntryWithItsValuesAsDefaults()
    {
        using var temp = new TempDir();
        var workspace = Load(temp, """{ "base": "workspace:params-demo", "name": "Demo release", "values": { "config": "Release" }, "extraArgs": "--fast" }""");
        var resolved = new CustomisationResolver(workspace).Resolve((ScriptNode)workspace.MyScripts.File.Scripts[0]);

        var shared = (ScriptNode)Flattener.ToShared(resolved, workspace.Workspace, new HashSet<string> { "params-demo" });

        Assert.AreEqual("demo-release", shared.Id);
        Assert.AreEqual("Demo release", shared.Name);
        Assert.AreEqual("--release", shared.Params!.Single(p => p.Name == "config").Default!.GetValue<string>());
        Assert.AreEqual("params_demo.py", shared.Path);
        CollectionAssert.AreEqual(new[] { "--fast" }, shared.Args);
        Assert.IsNull(shared.Base);
        Assert.IsNull(shared.Values);
        Assert.IsNull(shared.ExtraArgs);
    }

    [TestMethod]
    public void StandaloneEntryIsCopiedAsIs()
    {
        using var temp = new TempDir();
        var script = Path.Combine(temp.Root, "try.py");
        var workspace = Load(temp, $$"""{ "name": "Scratch", "path": "{{script.Replace("\\", "/")}}", "env": { "A": "1" } }""");
        var resolved = new CustomisationResolver(workspace).Resolve((ScriptNode)workspace.MyScripts.File.Scripts[0]);

        var shared = (ScriptNode)Flattener.ToShared(resolved, workspace.Workspace, new HashSet<string>());

        Assert.AreEqual("scratch", shared.Id);
        Assert.AreEqual(script.Replace("\\", "/"), shared.Path);
        Assert.AreEqual("1", shared.Env!["A"]);
    }

    [TestMethod]
    public void StandaloneCopyUsesAbsolutePathsAndQualifiedReferences()
    {
        using var temp = new TempDir();
        var workspace = Load(temp, null);
        var serve = workspace.Workspace.AllNodes().Select(n => n.Node).OfType<ScriptNode>().First(s => s.Stop is not null);

        var copy = Flattener.ToStandalone(serve, workspace.Workspace, "My server");

        Assert.IsNull(copy.Id);
        Assert.AreEqual("My server", copy.Name);
        Assert.AreEqual(Path.Combine(Fixtures.DemoWorkspace, serve.Path!), copy.Path, ignoreCase: true);
        Assert.AreEqual($"workspace:{serve.Stop}", copy.Stop);
    }

    private static LoadedWorkspace Load(TempDir temp, string? userEntry)
    {
        var paths = new AppPaths(temp.Path("data"));
        if (userEntry is not null)
        {
            var file = paths.UserFile("batchpad-demo");
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $$"""{ "scripts": [ {{userEntry}} ] }""");
        }
        return WorkspaceLoader.Load(Path.Combine(Fixtures.DemoWorkspace, "batchpad.json"), paths);
    }
}
