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

    [TestMethod]
    [DataRow(@"D:\proj\src\MtTypes.h(7,10): error C1083: Cannot open include file: 'p3/P3Types.h': No such file or directory")]
    [DataRow(@"D:\proj\build_targets.proj(9,5): error MSB4181: The ""MSBuild"" task returned false but did not log an error.")]
    [DataRow(@"LINK : fatal error LNK1168: cannot open game.exe for writing")]
    [DataRow("src/main.c:3:5: error: expected ';' before 'return'")]
    [DataRow("======================== BUILD FAILED ========================")]
    [DataRow("error C2220: the following warning is treated as an error")]
    [DataRow("FAILED tests/test_math.py::test_add - assert 1 == 2")]
    [DataRow("Traceback (most recent call last):")]
    [DataRow("ValueError: invalid literal for int()")]
    public void CompilerBuildAndTestErrorsReadAsErrors(string line) =>
        Assert.AreEqual(LineSeverity.Error, OutputLineParser.Plain.Parse(line).Severity);

    [TestMethod]
    [DataRow(@"D:\proj\src\a.cpp(12): warning C4996: 'strcpy': This function may be unsafe.")]
    [DataRow("src/main.c:3:5: warning: unused variable 'x'")]
    [DataRow("WARNING: the cache is stale")]
    [DataRow("DeprecationWarning: use run() instead")]
    public void CompilerAndToolWarningsReadAsWarnings(string line) =>
        Assert.AreEqual(LineSeverity.Warning, OutputLineParser.Plain.Parse(line).Severity);

    [TestMethod]
    [DataRow("    0 Warning(s)")]
    [DataRow("    0 Error(s)")]
    [DataRow(@"  ZombieFortCoreTests_vs2026.vcxproj -> D:\proj\build\release\tests\zombiefortcoretests.exe")]
    [DataRow("If a failure was LNK1168 \"cannot open ... for writing\", the target exe")]
    [DataRow("no errors found, 2 warnings suppressed")]
    public void OrdinaryAndSummaryLinesAreNotColoured(string line) =>
        Assert.AreEqual(LineSeverity.None, OutputLineParser.Plain.Parse(line).Severity);
}
