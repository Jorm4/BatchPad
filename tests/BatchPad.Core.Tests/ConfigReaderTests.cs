using System.Text.Json;
using BatchPad.Core.Choices;
using BatchPad.Core.Config;
using BatchPad.Core.Detection;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class ConfigReaderTests
{
    private static WorkspaceFile ReadAllNodes() => ConfigReader.ReadFile(Fixtures.Path("config", "all_nodes.json"));

    [TestMethod]
    public void ReadsEveryNodeType()
    {
        var file = ReadAllNodes();

        Assert.AreEqual("5c0f-demo", file.Id);
        Assert.HasCount(3, file.Scripts);
        var folder = Assert.IsInstanceOfType<FolderNode>(file.Scripts[0]);
        Assert.AreEqual("Build", folder.Folder);
        Assert.IsTrue(folder.Collapsed);
        var script = Assert.IsInstanceOfType<ScriptNode>(folder.Items.Single());
        Assert.AreEqual(Runner.Batch, script.Runner);
        Assert.AreEqual(ConsoleMode.WindowKeepOpen, script.Console);
        Assert.AreEqual(ArtifactOpen.OnSuccess, script.Artifacts![0].Open);

        var workflow = Assert.IsInstanceOfType<WorkflowNode>(file.Scripts[1]);
        Assert.HasCount(2, workflow.Steps);
        Assert.AreEqual(StepWhen.Always, workflow.Steps[1].When);
        Assert.AreEqual("SpaceTrader", workflow.Steps[0].Values!["apps"]![0]!.GetValue<string>());

        var link = Assert.IsInstanceOfType<LinkNode>(file.Scripts[2]);
        Assert.AreEqual("qa_report.html", link.Url);

        Assert.AreEqual("tools", file.ScriptFolders![1].Path);
        Assert.IsFalse(file.ScriptFolders[1].Recurse);
    }

    [TestMethod]
    public void OversizedFilesAreProblemsNotReads()
    {
        using var dir = new TempDir();
        foreach (var name in new[] { "batchpad.json", "huge.txt", "huge.bat" })
        {
            using var stream = File.Create(dir.Path(name));
            stream.SetLength(ConfigReader.MaxFileBytes + 1);
        }

        var loaded = WorkspaceLoader.Load(dir.Path("batchpad.json"), new AppPaths(dir.Path("data")));
        var choices = new ChoiceResolver().Resolve(
            new ParameterDefinition { ChoicesFrom = [new ChoiceSource { File = "huge.txt", Regex = "." }] }, new ChoiceContext(dir.Root));

        StringAssert.Contains(loaded.Errors.Single().Message, "larger than 8 MB");
        StringAssert.Contains(choices.Problems.Single(), "larger than 8 MB");
        Assert.IsEmpty(Detector.Detect(dir.Path("huge.bat")).Parameters);
    }

    [TestMethod]
    public void ReadsParameterFeatures()
    {
        var script = (ScriptNode)((FolderNode)ReadAllNodes().Scripts[0]).Items[0];
        var config = script.Params![0];
        var apps = script.Params[1];
        var driver = script.Params[2];

        Assert.AreEqual(ParameterType.Choice, config.Type);
        Assert.AreEqual("--release", config.Default!.GetValue<string>());
        Assert.AreEqual(ParameterType.Multichoice, apps.Type);
        Assert.AreEqual("end", apps.Position);
        Assert.AreEqual("all", apps.EmptyMeans);
        CollectionAssert.AreEqual(new[] { "--tests" }, apps.EmptyArgs);
        Assert.AreEqual(9, apps.MaxPerCall);
        Assert.HasCount(2, apps.ChoicesFrom!);
        Assert.AreEqual(" ", apps.ChoicesFrom![0].Split);
        Assert.IsTrue(apps.ChoicesFrom![1].Stem);
        Assert.AreEqual("GAME_AUDIO_DRIVER", driver.EnvVar);
        Assert.IsFalse(driver.Emit);
    }

    [TestMethod]
    public void AUserFileWithEveryTriggerKindRoundTrips()
    {
        var file = ConfigReader.ReadFile(Fixtures.Path("config", "user_schedules.json"));
        var written = JsonSerializer.Serialize(file, ConfigJson.Options);
        var reread = ConfigReader.Parse(written);

        Assert.AreEqual(written, JsonSerializer.Serialize(reread, ConfigJson.Options));
        var schedules = reread.Schedules!;
        CollectionAssert.AreEqual(
            new[] { TriggerKind.Cron, TriggerKind.Every, TriggerKind.At, TriggerKind.FileChanged, TriggerKind.OnStart, TriggerKind.AfterRun },
            schedules.Select(s => s.Trigger.Kind).ToArray());
        var nightly = schedules[0];
        Assert.AreEqual("nightly", nightly.Key);
        Assert.AreEqual(MissedPolicy.RunOnce, nightly.Missed);
        Assert.AreEqual(OverlapPolicy.Queue, nightly.Overlap);
        Assert.IsTrue(nightly.Enabled);
        Assert.AreEqual("RallyRacer", nightly.Values!["app"]!.GetValue<string>());
        Assert.AreEqual("09:00-18:00", schedules[1].Trigger.Between);
        Assert.AreEqual("5s", schedules[3].Trigger.Debounce);
        Assert.AreEqual(AfterRunResult.Failure, schedules[5].Trigger.Result);
        Assert.AreNotEqual(schedules[1].Key, schedules[2].Key);
        StringAssert.Contains(written, "\"missed\": \"runOnce\"");
    }

    [TestMethod]
    public void ReadsPlainAndRichChoices()
    {
        var script = (ScriptNode)((FolderNode)ReadAllNodes().Scripts[0]).Items[0];
        var choices = script.Params![0].Choices!;

        Assert.AreEqual("", choices[0].Value);
        Assert.AreEqual("Debug", choices[0].Label);
        Assert.AreEqual("debug", choices[0].Fields["dir"].GetString());
        Assert.IsTrue(choices[1].Split);
        Assert.IsTrue(choices[2].IsPlain);
        Assert.AreEqual("plain", choices[2].DisplayLabel);
    }

    [TestMethod]
    public void SingleChoiceSourceBecomesOneElementList()
    {
        var shared = ReadAllNodes().SharedParams!["app"];

        Assert.AreEqual("games", shared.ChoicesFrom!.Single().List);
        Assert.AreEqual("lower", shared.ChoicesFrom![0].ValueTransform);
    }

    [TestMethod]
    public void UnknownPropertiesSurviveReadSerializeRead()
    {
        var json = JsonSerializer.Serialize(ReadAllNodes(), ConfigJson.Options);
        var reread = ConfigReader.Parse(json);

        CollectionAssert.AreEqual(new[] { 1, 2, 3 }, reread.ExtensionData!["futureTopLevel"].GetProperty("nested").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        var script = (ScriptNode)((FolderNode)reread.Scripts[0]).Items[0];
        Assert.AreEqual("kept", script.ExtensionData!["futureScriptField"].GetString());
        Assert.AreEqual(7, script.Params![2].ExtensionData!["futureParamField"].GetInt32());
        var workflow = (WorkflowNode)reread.Scripts[1];
        Assert.AreEqual(4, workflow.Steps[1].MaxParallel);
    }

    [TestMethod]
    public void MalformedJsonReportsTheSource()
    {
        var ex = Assert.ThrowsExactly<ConfigException>(() => ConfigReader.Parse("{ \"scripts\": [ { ] }", "broken.json"));
        StringAssert.StartsWith(ex.Message, "broken.json:");
    }

    [TestMethod]
    [DataRow("""{ "scripts": [ { "command": "x", "params": [ { "name": "a", "type": "choice", "choices": [ { "value": "v", "label": 5 } ] } ] } ] }""")]
    [DataRow("""{ "scripts": [ { "command": "x", "params": [ { "name": "a", "type": "choice", "choices": [ { "value": "v", "split": "yes" } ] } ] } ] }""")]
    [DataRow("""{ "scripts": [ { "steps": [ { "parallel": 1.5 } ] } ] }""")]
    [DataRow("""{ "scripts": [ { "steps": [ { "parallel": 99999999999 } ] } ] }""")]
    public void WrongValueTypesAreConfigErrors(string json)
    {
        var ex = Assert.ThrowsExactly<ConfigException>(() => ConfigReader.Parse(json, "typed.json"));
        StringAssert.StartsWith(ex.Message, "typed.json:");
    }
}
