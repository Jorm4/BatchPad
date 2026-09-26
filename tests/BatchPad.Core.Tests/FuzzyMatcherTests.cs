using BatchPad.Core.Search;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class FuzzyMatcherTests
{
    private static readonly string[] DemoItems =
    [
        "hello.bat", "hello.py", "hello.cs", "hello.ps1", "Parameters demo", "Serve demo", "Stop demo server", "Build & run",
        "Build all, then relink", "Build Release & run SpaceTrader",
    ];

    [TestMethod]
    public void WordStartsRankFirst()
    {
        var ranked = FuzzyMatcher.Rank("bldrl", DemoItems, s => s).ToList();

        Assert.AreEqual("Build Release & run SpaceTrader", ranked[0]);
        CollectionAssert.DoesNotContain(ranked, "Build & run");
    }

    [TestMethod]
    public void MatchesAreCaseInsensitiveSubsequences()
    {
        Assert.IsNotNull(FuzzyMatcher.Score("HB", "hello.bat"));
        Assert.IsNull(FuzzyMatcher.Score("bh", "hello.bat"));
        Assert.AreEqual(0, FuzzyMatcher.Score("  ", "anything"));
        Assert.AreEqual("hello.bat", FuzzyMatcher.Rank("hello.bat", DemoItems, s => s).Single());
    }

    [TestMethod]
    public void ConsecutiveLettersBeatScatteredOnes()
    {
        Assert.IsGreaterThan(FuzzyMatcher.Score("serve", "Stop demo server")!.Value, FuzzyMatcher.Score("serve", "Serve demo")!.Value);
        Assert.AreEqual("Serve demo", FuzzyMatcher.Rank("serve", DemoItems, s => s).First());
    }
}
