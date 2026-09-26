using BatchPad.Core.Customisation;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class CustomisationTests
{
    private const string BuildScript = """
        { "id": "build", "name": "Build", "path": "build.bat", "env": { "A": "base", "B": "base" },
          "nameTemplate": "Build ${param:config.label} & run ${param:app}",
          "params": [
            { "name": "config", "type": "choice", "choices": [ { "value": "--debug", "label": "Debug" }, { "value": "--release", "label": "Release" } ] }
            PARAMS
          ] }
        """;

    [TestMethod]
    public void CustomisationPicksUpAParameterAddedToItsBaseLater()
    {
        using var temp = new TempDir();
        WriteWorkspace(temp, "");
        WriteUser(temp, """{ "base": "workspace:build", "values": { "config": "Release" }, "env": { "B": "mine" }, "console": "window" }""");

        var before = ResolveFirst(temp);
        Assert.HasCount(1, before.Definition!.Params!);
        Assert.AreEqual("--release", before.Values["config"]!.GetValue<string>(), "a label is normalised to its value");
        Assert.AreEqual(ConsoleMode.Window, ((ScriptNode)before.Definition).Console);
        Assert.AreEqual("base", before.Definition.Env!["A"]);
        Assert.AreEqual("mine", before.Definition.Env["B"]);

        WriteWorkspace(temp, """, { "name": "app", "type": "text", "default": "Kart" }""");
        var after = ResolveFirst(temp);

        CollectionAssert.AreEqual(new[] { "config", "app" }, after.Definition!.Params!.Select(p => p.Name).ToArray());
        Assert.AreEqual("Build Release & run Kart", after.Name);
        Assert.AreEqual(Path.GetFullPath(temp.Root), after.DefinitionTree!.BaseDirectory);
    }

    [TestMethod]
    public void MissingBaseGivesABrokenEntryWithItsValuesIntact()
    {
        using var temp = new TempDir();
        WriteWorkspace(temp, "");
        WriteUser(temp, """{ "base": "workspace:gone", "name": "Old favourite", "values": { "config": "--release" } }""");

        var resolved = ResolveFirst(temp);

        Assert.IsTrue(resolved.IsBroken);
        Assert.IsNull(resolved.Definition);
        Assert.AreEqual("Old favourite", resolved.Name);
        Assert.AreEqual("--release", resolved.Values["config"]!.GetValue<string>());
        Assert.ThrowsExactly<InvalidOperationException>(() => resolved.ToRunRequest(Load(temp)));
    }

    [TestMethod]
    public void NameTemplateNamesAnUnnamedCustomisation()
    {
        using var temp = new TempDir();
        WriteWorkspace(temp, """, { "name": "app", "type": "text" }""");
        WriteUser(temp, """{ "base": "workspace:build", "values": { "config": "--release", "app": "SpaceTrader" }, "extraArgs": "-v" }""");

        var resolved = ResolveFirst(temp);

        Assert.AreEqual("Build Release & run SpaceTrader", resolved.Name);
        var request = resolved.ToRunRequest(Load(temp));
        Assert.AreEqual("-v", request.ExtraArguments);
        Assert.AreEqual("SpaceTrader", request.Values!["app"]!.GetValue<string>());
    }

    [TestMethod]
    public void TwoUserStoresSavingDifferentEntriesBothSurvive()
    {
        using var temp = new TempDir();
        var file = temp.Path("user.json");
        var first = new UserStore(file);
        var second = new UserStore(file);
        _ = first.Load();
        _ = second.Load();

        var firstId = first.Add(new ScriptNode { Base = "workspace:build", Name = "Build · Kart" }, folder: "Daily");
        var secondId = second.Add(new ScriptNode { Base = "workspace:build", Name = "Build · Kart" });
        second.Replace(new ScriptNode { Id = firstId }, new ScriptNode { Id = firstId, Base = "workspace:build", Name = "Renamed" });

        var saved = new UserStore(file).Load();
        var daily = saved.Scripts.OfType<FolderNode>().Single();
        Assert.AreEqual("build-kart", firstId);
        Assert.AreEqual("build-kart-2", secondId);
        Assert.AreEqual("Renamed", ((ScriptNode)daily.Items.Single()).Name);
        Assert.AreEqual(secondId, saved.Scripts.OfType<ScriptNode>().Single().Id);
    }

    [TestMethod]
    public void WorkflowCustomisationKeepsItsStepValues()
    {
        using var temp = new TempDir();
        WriteWorkspace(temp, "", """, { "id": "sweep", "steps": [ { "id": "report", "run": "build" } ] }""");
        WriteUser(temp, """{ "base": "workspace:sweep", "name": "Nightly", "stepValues": { "report": { "config": "--debug" } } }""");

        var resolved = ResolveFirst(temp);

        Assert.IsInstanceOfType<WorkflowNode>(resolved.Definition);
        Assert.AreEqual("--debug", resolved.StepValues["report"]["config"]!.GetValue<string>());
    }

    private static void WriteWorkspace(TempDir temp, string extraParams, string extraScripts = "") =>
        File.WriteAllText(temp.Path("batchpad.json"),
            $$"""{ "id": "custom-test", "scripts": [ {{BuildScript.Replace("PARAMS", extraParams)}} {{extraScripts}} ] }""");

    private static void WriteUser(TempDir temp, string entry)
    {
        var file = Paths(temp).UserFile("custom-test");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, $$"""{ "scripts": [ {{entry}} ] }""");
    }

    private static AppPaths Paths(TempDir temp) => new(temp.Path("data"));

    private static LoadedWorkspace Load(TempDir temp) => WorkspaceLoader.Load(temp.Path("batchpad.json"), Paths(temp));

    private static ResolvedCustomisation ResolveFirst(TempDir temp)
    {
        var workspace = Load(temp);
        Assert.IsEmpty(workspace.Errors, string.Join('\n', workspace.Errors));
        return new CustomisationResolver(workspace).Resolve((ScriptNode)workspace.MyScripts.File.Scripts[0]);
    }
}
