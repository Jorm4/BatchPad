using BatchPad.Core.Output;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class OutputLineParserTests
{
    [TestMethod]
    public void SgrCodesSplitALineIntoStyledSpans()
    {
        var line = OutputLineParser.Plain.Parse("\u001b[31mred\u001b[0m plain");

        Assert.AreEqual("red plain", line.Text);
        CollectionAssert.AreEqual(new[]
        {
            new OutputSpan("red", AnsiColor.Red, false),
            new OutputSpan(" plain", AnsiColor.Default, false),
        }, line.Spans!.ToList());
    }

    [TestMethod]
    public void BoldAndBrightColoursCombine()
    {
        var line = OutputLineParser.Plain.Parse("\u001b[1;92mOK\u001b[22m done\u001b[39m.");

        CollectionAssert.AreEqual(new[]
        {
            new OutputSpan("OK", AnsiColor.BrightGreen, true),
            new OutputSpan(" done", AnsiColor.BrightGreen, false),
            new OutputSpan(".", AnsiColor.Default, false),
        }, line.Spans!.ToList());
    }

    [TestMethod]
    public void UnknownEscapesAreStrippedNotShown()
    {
        var line = OutputLineParser.Plain.Parse("\u001b[2Kprogress\u001b]0;title\u0007 50%\u001b[?25h");

        Assert.AreEqual("progress 50%", line.Text);
        Assert.IsNull(line.Spans);
    }

    [TestMethod]
    public void APlainLineHasNoSpans()
    {
        var line = OutputLineParser.Plain.Parse("hello");

        Assert.AreEqual("hello", line.Text);
        Assert.IsNull(line.Spans);
        Assert.IsFalse(line.IsErrorMatch);
    }

    [TestMethod]
    public void ALineMatchingAnErrorPatternIsFlagged()
    {
        var parser = new OutputLineParser([@"LNK\d{4}", "(unclosed"]);

        Assert.IsTrue(parser.Parse("main.obj : error LNK2019: unresolved external symbol").IsErrorMatch);
        Assert.IsTrue(parser.Parse("\u001b[31mLNK1120\u001b[0m: 1 unresolved externals").IsErrorMatch);
        Assert.IsFalse(parser.Parse("Linking...").IsErrorMatch);
    }

    [TestMethod]
    public void APatternThatTimesOutDoesNotHideTheNextOne()
    {
        var parser = new OutputLineParser(["^(a+)+$", "error"]);

        Assert.IsTrue(parser.Parse(new string('a', 5000) + "! error").IsErrorMatch);
    }
}
