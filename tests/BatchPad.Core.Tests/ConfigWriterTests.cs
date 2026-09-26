using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class ConfigWriterTests
{
    private static WorkspaceFile ReadAllNodes() => ConfigReader.ReadFile(Fixtures.Path("config", "all_nodes.json"));

    private static JsonNode ToNode(WorkspaceFile file) => JsonSerializer.SerializeToNode(file, ConfigJson.Options)!;

    [TestMethod]
    public void WriteThenReadGivesEqualModel()
    {
        using var dir = new TempDir();
        var original = ReadAllNodes();

        ConfigWriter.Write(dir.Path("batchpad.json"), original);
        var reread = ConfigReader.ReadFile(dir.Path("batchpad.json"));

        Assert.IsTrue(JsonNode.DeepEquals(ToNode(original), ToNode(reread)));
        Assert.AreEqual(ConfigWriter.Serialize(original), File.ReadAllText(dir.Path("batchpad.json")));
    }

    [TestMethod]
    public void ChangingOneFieldChangesExactlyOneLine()
    {
        var file = ReadAllNodes();
        var before = ConfigWriter.Serialize(file).Split('\n');

        ((ScriptNode)((FolderNode)file.Scripts[0]).Items[0]).Name = "Build everything";
        var after = ConfigWriter.Serialize(file).Split('\n');

        Assert.HasCount(before.Length, after);
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]).ToList();
        Assert.HasCount(1, changed);
        StringAssert.Contains(after[changed[0]], "\"name\": \"Build everything\"");
    }

    [TestMethod]
    public void OutputIsStableTwoSpaceIndentedAndUnescaped()
    {
        var text = ConfigWriter.Serialize(ReadAllNodes());

        Assert.AreEqual(text, ConfigWriter.Serialize(ConfigReader.Parse(text)));
        StringAssert.StartsWith(text, "{\n  \"$schema\":");
        StringAssert.Contains(text, "{\n          \"id\": \"build\",\n          \"name\": \"Build\",\n");
        StringAssert.Contains(text, "\"name\": \"Build & run\"");
        Assert.DoesNotContain("\r", text);
    }

    [TestMethod]
    public void DefaultValuesAreNotWritten()
    {
        var file = new WorkspaceFile
        {
            ScriptFolders = [new ScriptFolder { Path = "tools" }],
            Scripts =
            [
                new FolderNode { Folder = "Empty" },
                new ScriptNode
                {
                    Path = "a.bat",
                    Params = [new ParameterDefinition { Name = "p", Emit = false, ChoicesFrom = [new ChoiceSource { Glob = "*.x" }] }],
                },
            ],
        };

        var text = ConfigWriter.Serialize(file);

        Assert.DoesNotContain("collapsed", text);
        Assert.DoesNotContain("groupByPrefix", text);
        Assert.DoesNotContain("stem", text);
        Assert.DoesNotContain("null", text);
        StringAssert.Contains(text, "\"items\": []");
        StringAssert.Contains(text, "\"emit\": false");
    }

    [TestMethod]
    public void CrashBetweenTempWriteAndRenameLeavesOriginalIntact()
    {
        using var dir = new TempDir();
        var path = dir.Path("batchpad.json");
        File.WriteAllText(path, "original");

        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ConfigWriter.WriteAtomic(path, "replacement", beforeReplace: () => throw new InvalidOperationException()));

        Assert.AreEqual("original", File.ReadAllText(path));
        CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(dir.Root));
    }

    [TestMethod]
    public void UpdateRereadsSoConcurrentEditsBothSurvive()
    {
        using var dir = new TempDir();
        var path = dir.Path("user.json");
        ConfigWriter.Write(path, new WorkspaceFile());
        var staleCopyInWindowA = ConfigReader.ReadFile(path);

        ConfigWriter.Update(path, file => file.Scripts.Add(new ScriptNode { Name = "From window B" }));
        ConfigWriter.Update(path, file => file.Scripts.Add(new ScriptNode { Name = "From window A" }));

        var names = ConfigReader.ReadFile(path).Scripts.Cast<ScriptNode>().Select(s => s.Name).ToList();
        CollectionAssert.AreEqual(new[] { "From window B", "From window A" }, names);
        Assert.IsEmpty(staleCopyInWindowA.Scripts);
    }

    [TestMethod]
    public void UpdateCreatesAMissingFile()
    {
        using var dir = new TempDir();
        var path = dir.Path("sub", "user.json");

        ConfigWriter.Update(path, file => file.Id = "abc");

        Assert.AreEqual("abc", ConfigReader.ReadFile(path).Id);
    }
}
