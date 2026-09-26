using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class ArgumentAssemblerTests
{
    private static readonly TemplateContext Templates = new() { WorkspaceDir = @"C:\repo", Environment = _ => null };

    private static IReadOnlyList<Invocation> Assemble(ScriptNode script, Dictionary<string, JsonNode?>? values = null,
        Dictionary<string, ParameterDefinition>? shared = null) =>
        ArgumentAssembler.Assemble(new AssemblyRequest(script, Templates) { Values = values, SharedParams = shared });

    private static List<string> Arguments(ScriptNode script, Dictionary<string, JsonNode?>? values = null) =>
        [.. Assemble(script, values).Single().Arguments];

    private static ParameterDefinition Apps => new()
    {
        Name = "apps",
        Type = ParameterType.Multichoice,
        MaxPerCall = 9,
        EmptyMeans = "all",
        EmptyArgs = ["--tests"],
        Position = "end",
    };

    private static ChoiceDefinition Choice(string value, string label, bool split = false, string? dir = null)
    {
        var choice = new ChoiceDefinition { Value = value, Label = label, Split = split };
        if (dir is not null)
            choice.Fields["dir"] = JsonDocument.Parse($"\"{dir}\"").RootElement;
        return choice;
    }

    [TestMethod]
    public void MaxPerCallSplitsALargeSelectionIntoSeveralInvocations()
    {
        var script = new ScriptNode { Path = "build.bat", Args = ["--no-logo"], Params = [Apps] };
        var apps = new JsonArray(Enumerable.Range(1, 12).Select(i => (JsonNode?)JsonValue.Create($"App{i}")).ToArray());

        var invocations = Assemble(script, new() { ["apps"] = apps });

        Assert.HasCount(2, invocations);
        string[] firstNine = ["--no-logo", .. Enumerable.Range(1, 9).Select(i => $"App{i}")];
        CollectionAssert.AreEqual(firstNine, invocations[0].Arguments.ToList());
        CollectionAssert.AreEqual(new[] { "--no-logo", "App10", "App11", "App12" }, invocations[1].Arguments.ToList());
    }

    [TestMethod]
    public void EmptyMultichoiceEmitsEmptyArgs()
    {
        var script = new ScriptNode { Path = "build.bat", Params = [Apps] };

        CollectionAssert.AreEqual(new[] { "--tests" }, Arguments(script, new() { ["apps"] = new JsonArray() }));
    }

    [TestMethod]
    public void ArgEndingInEqualsJoinsTheValue()
    {
        var tier = new ParameterDefinition { Name = "tier", Type = ParameterType.Choice, Arg = "--benchmark_filter=", Default = "^Smoke" };

        CollectionAssert.AreEqual(new[] { "--benchmark_filter=^Smoke" }, Arguments(new ScriptNode { Path = "bench.exe", Params = [tier] }));
    }

    [TestMethod]
    public void RichChoiceWithSplitGivesSeveralArgumentsAndFieldsReachTemplates()
    {
        var config = new ParameterDefinition
        {
            Name = "config",
            Type = ParameterType.Choice,
            Choices = [Choice("", "Debug", dir: "debug"), Choice("--release --asan", "Release ASan", split: true, dir: "release")],
        };
        var script = new ScriptNode { Path = "build.bat", Args = ["--out=build/${param:config.dir}"], Params = [config] };

        CollectionAssert.AreEqual(new[] { "--out=build/release", "--release", "--asan" }, Arguments(script, new() { ["config"] = "--release --asan" }));
        CollectionAssert.AreEqual(new[] { "--out=build/debug" }, Arguments(script, new() { ["config"] = "" }));
    }

    [TestMethod]
    public void AGivenLabelMatchingOneChoiceIsPassedAsItsValue()
    {
        var game = new ParameterDefinition { Name = "game", Type = ParameterType.Choice, Arg = "--game",
            Choices = [Choice("robotmanager", "RobotManager"), Choice("spacetrader", "SpaceTrader")] };
        var games = new ParameterDefinition { Name = "games", Type = ParameterType.Multichoice, Choices = game.Choices };
        var script = new ScriptNode { Path = "crawl.bat", Params = [game, games] };

        CollectionAssert.AreEqual(new[] { "--game", "robotmanager", "spacetrader", "unknown" },
            Arguments(script, new() { ["game"] = "RobotManager", ["games"] = new JsonArray("SpaceTrader", "unknown") }));
    }

    [TestMethod]
    public void AGivenLabelOfSeveralChoicesIsAnError()
    {
        var tier = new ParameterDefinition { Name = "tier", Type = ParameterType.Choice, Choices = [Choice("^Smoke", "Fast"), Choice("^Unit", "Fast")] };

        Assert.ThrowsExactly<ArgumentAssemblyException>(() => Arguments(new ScriptNode { Path = "b.bat", Params = [tier] }, new() { ["tier"] = "Fast" }));
    }

    [TestMethod]
    public void EnvVarParameterSetsEnvironmentAndAddsNoArgument()
    {
        var driver = new ParameterDefinition { Name = "audio", Type = ParameterType.Text, EnvVar = "GAME_AUDIO_DRIVER", Default = "dummy" };
        var script = new ScriptNode { Path = "game.exe", Env = new() { ["MODE"] = "${workspaceDir}" }, Params = [driver] };

        var invocation = Assemble(script).Single();

        Assert.IsEmpty(invocation.Arguments);
        Assert.AreEqual("dummy", invocation.Environment["GAME_AUDIO_DRIVER"]);
        Assert.AreEqual(@"C:\repo", invocation.Environment["MODE"]);
        Assert.AreEqual("GAME_AUDIO_DRIVER=dummy game.exe", invocation.Display);
    }

    [TestMethod]
    public void FlagsTextSplitRepeatArgPositionEndAndEmitFalse()
    {
        var script = new ScriptNode
        {
            Path = "run.bat",
            Params =
            [
                new() { Name = "targets", Type = ParameterType.Text, Split = true, Position = "end" },
                new() { Name = "verbose", Type = ParameterType.Flag, Arg = "--verbose" },
                new() { Name = "quiet", Type = ParameterType.Flag, Arg = "--quiet" },
                new() { Name = "filter", Type = ParameterType.Multichoice, Arg = "--filter", RepeatArg = true },
                new() { Name = "port", Type = ParameterType.Int, Arg = "--port", Default = 8123, Emit = false },
                new() { Name = "name", Type = ParameterType.Text, Arg = "--name" },
            ],
        };
        var values = new Dictionary<string, JsonNode?>
        {
            ["targets"] = "a \"b c\"",
            ["verbose"] = true,
            ["filter"] = new JsonArray("X", "Y"),
            ["name"] = "two words",
        };

        var invocation = Assemble(script, values).Single();

        CollectionAssert.AreEqual(
            new[] { "--verbose", "--filter", "X", "--filter", "Y", "--name", "two words", "a", "b c" },
            invocation.Arguments.ToList());
        Assert.AreEqual("run.bat --verbose --filter X --filter Y --name \"two words\" a \"b c\"", invocation.Display);
    }

    [TestMethod]
    public void UseMergesTheSharedParameterWithOverrides()
    {
        var shared = new Dictionary<string, ParameterDefinition>
        {
            ["app"] = new() { Type = ParameterType.Choice, Arg = "--app", Choices = [new() { Value = "Alpha" }], Default = "Alpha" },
        };
        var script = new ScriptNode { Path = "run.bat", Params = [new() { Use = "app", Arg = "--target" }] };

        CollectionAssert.AreEqual(new[] { "--target", "Alpha" }, Assemble(script, shared: shared).Single().Arguments.ToList());
        Assert.AreEqual("--app", shared["app"].Arg);
    }

    [TestMethod]
    public void UnknownUseIsAnError()
    {
        var script = new ScriptNode { Path = "run.bat", Params = [new() { Use = "missing" }] };

        Assert.ThrowsExactly<ArgumentAssemblyException>(() => Assemble(script));
    }

    private static ScriptNode TemplatedServer => new()
    {
        Path = "serve.py",
        Args = ["-u"],
        ArgsTemplate = ["{config}", "--port", "{port}", "{targets...}"],
        Params =
        [
            new() { Name = "config", Type = ParameterType.Text, Arg = "--config" },
            new() { Name = "port", Type = ParameterType.Text },
            new() { Name = "targets", Type = ParameterType.Multichoice },
        ],
    };

    [TestMethod]
    public void ArgsTemplateSetsTheOrderAfterFixedArgs()
    {
        var arguments = Arguments(TemplatedServer, new()
        {
            ["config"] = "dev.json",
            ["port"] = "8080",
            ["targets"] = new JsonArray("a", "b", "c"),
        });

        CollectionAssert.AreEqual(new[] { "-u", "dev.json", "--port", "8080", "a", "b", "c" }, arguments);
    }

    [TestMethod]
    public void ArgsTemplateDropsAnElementThatExpandsToNothing()
    {
        var arguments = Arguments(TemplatedServer, new() { ["port"] = "80", ["targets"] = new JsonArray("x") });

        CollectionAssert.AreEqual(new[] { "-u", "--port", "80", "x" }, arguments);
    }
}
