using BatchPad.Core.Choices;
using BatchPad.Core.Model;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class ChoiceResolverTests
{
    private static readonly ChoiceContext Context = new(Fixtures.Path("choices"))
    {
        Lists = new Dictionary<string, List<string>> { ["games"] = ["Tetris", "Pong"] },
    };

    private static List<ChoiceDefinition> Resolve(params ChoiceSource[] sources) => Resolve(new ParameterDefinition { ChoicesFrom = [.. sources] });

    private static List<ChoiceDefinition> Resolve(ParameterDefinition parameter)
    {
        var result = new ChoiceResolver().Resolve(parameter, Context);
        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        return [.. result.Choices];
    }

    private static List<string> Values(IEnumerable<ChoiceDefinition> choices) => choices.Select(c => c.Value).ToList();

    [TestMethod]
    public void RegexWithSplitGivesOneChoicePerWord()
    {
        var choices = Resolve(new ChoiceSource { File = "build.bat", Regex = "set \"GAMES=([^\"]*)\"", Split = " " });

        CollectionAssert.AreEqual(new[] { "Asteroids", "RobotManager", "Tetris" }, Values(choices));
    }

    [TestMethod]
    public void RegexAllWithLowerTransformKeepsLabelAndLowersValue()
    {
        var choices = Resolve(new ChoiceSource { File = "CMakeLists.txt", Regex = @"add_game_app\((\w+)", All = true, ValueTransform = "lower" });

        CollectionAssert.AreEqual(new[] { "asteroids", "robotmanager", "tetris" }, Values(choices));
        var robot = choices[1];
        Assert.AreEqual("RobotManager", robot.Label);
        Assert.AreEqual("robotmanager", robot.Value);
    }

    [TestMethod]
    public void GlobResultsAreRelativeStemmedOrCaptured()
    {
        CollectionAssert.AreEqual(new[] { "web/alpha.html", "web/beta.html" }, Values(Resolve(new ChoiceSource { Glob = "web/*.html" })));
        CollectionAssert.AreEqual(new[] { "alpha.html", "beta.html" }, Values(Resolve(new ChoiceSource { Glob = "web/*.html", RelativeTo = "web" })));
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, Values(Resolve(new ChoiceSource { Glob = "web/*.html", Stem = true })));
        CollectionAssert.AreEqual(new[] { "alpha", "beta" }, Values(Resolve(new ChoiceSource { Glob = "**/*.html", Match = @"/(\w+)\.html" })));
    }

    [TestMethod]
    public void FixedChoicesAndSourcesMergeWithoutDuplicates()
    {
        var parameter = new ParameterDefinition
        {
            Choices = [new ChoiceDefinition { Value = "Asteroids" }],
            ChoicesFrom =
            [
                new ChoiceSource { File = "build.bat", Regex = "set \"GAMES=([^\"]*)\"", Split = " " },
                new ChoiceSource { List = "games" },
            ],
        };

        CollectionAssert.AreEqual(new[] { "Asteroids", "RobotManager", "Tetris", "Pong" }, Values(Resolve(parameter)));
    }

    [TestMethod]
    public void BrokenSourceIsReportedAndOthersStillResolve()
    {
        var parameter = new ParameterDefinition { ChoicesFrom = [new ChoiceSource { List = "missing" }, new ChoiceSource { List = "games" }] };

        var result = new ChoiceResolver().Resolve(parameter, Context);

        Assert.HasCount(1, result.Problems);
        CollectionAssert.AreEqual(new[] { "Tetris", "Pong" }, Values(result.Choices));
    }

    [TestMethod]
    public void RegexResultIsCachedUntilTheFileChanges()
    {
        using var temp = new TempDir();
        var file = temp.Path("list.txt");
        File.WriteAllText(file, "A=one");
        var parameter = new ParameterDefinition { ChoicesFrom = [new ChoiceSource { File = "list.txt", Regex = "A=(\\w+)" }] };
        var resolver = new ChoiceResolver();
        var context = new ChoiceContext(temp.Root);

        Assert.AreEqual("one", resolver.Resolve(parameter, context).Choices.Single().Value);
        File.WriteAllText(file, "A=two");
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddSeconds(5));

        Assert.AreEqual("two", resolver.Resolve(parameter, context).Choices.Single().Value);
    }

    [TestMethod]
    public void StoredLabelNormalisesToValueAndAmbiguousLabelIsRejected()
    {
        List<ChoiceDefinition> config =
        [
            new() { Value = "", Label = "Debug" },
            new() { Value = "--release", Label = "Release" },
            new() { Value = "--final", Label = "Retail" },
            new() { Value = "--final --asan", Label = "Retail" },
        ];

        var release = ValueNormalizer.Normalize("Release", config);
        Assert.AreEqual("--release", release.Value);
        Assert.IsTrue(release.Changed);

        var value = ValueNormalizer.Normalize("--release", config);
        Assert.IsFalse(value.Changed);
        Assert.IsTrue(value.IsValid);

        var ambiguous = ValueNormalizer.Normalize("Retail", config);
        Assert.IsFalse(ambiguous.IsValid);
        Assert.AreEqual("Retail", ambiguous.Value);
    }
}
