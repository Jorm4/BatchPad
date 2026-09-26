using BatchPad.Core.Choices;
using BatchPad.Core.Model;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class PatternFromExampleTests
{
    private static List<string> Apply(string content, string line, string example)
    {
        var derived = PatternFromExample.Derive(line, line.IndexOf(example, StringComparison.Ordinal), example.Length, content.Split('\n'));
        Assert.IsNotNull(derived);
        using var temp = new TempDir();
        File.WriteAllText(temp.Path("source.txt"), content);
        var source = new ChoiceSource { File = "source.txt", Regex = derived.Regex, Split = derived.Split, All = derived.All };
        var result = new ChoiceResolver().Resolve(new ParameterDefinition { ChoicesFrom = [source] }, new ChoiceContext(temp.Root));
        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        return result.Choices.Select(c => c.Value).ToList();
    }

    [TestMethod]
    public void ExampleInASeparatedListGivesTheWholeList()
    {
        const string content = "@echo off\r\nset \"APPS=Alpha Beta Gamma\"\r\nset \"OTHER=Delta\"\r\n";

        var values = Apply(content, "set \"APPS=Alpha Beta Gamma\"", "Beta");

        CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Gamma" }, values);
    }

    [TestMethod]
    public void ExampleOnOneOfSimilarLinesGivesEveryName()
    {
        var content = File.ReadAllText(Fixtures.Path("choices", "CMakeLists.txt"));

        var values = Apply(content, "add_game_app(RobotManager src/robots)", "RobotManager");

        CollectionAssert.AreEqual(new[] { "Asteroids", "RobotManager", "Tetris" }, values);
    }

    [TestMethod]
    public void EmptySelectionDerivesNothing() => Assert.IsNull(PatternFromExample.Derive("set X=1", 2, 0));
}
