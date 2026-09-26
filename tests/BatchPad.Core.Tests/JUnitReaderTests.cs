using System.Text.Json;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class JUnitReaderTests
{
    [TestMethod]
    public void PytestReportHasCountsAndFailureMessages()
    {
        var report = JUnitReader.Read(Fixtures.Path("junit", "pytest.xml"));

        Assert.AreEqual((2, 2, 1), (report.Count(TestOutcome.Passed), report.Count(TestOutcome.Failed), report.Count(TestOutcome.Skipped)));
        var divide = report.Cases.Single(c => c.Name == "test_divide");
        Assert.AreEqual("tests.test_math", divide.ClassName);
        Assert.AreEqual("ZeroDivisionError: division by zero", divide.Message);
        StringAssert.Contains(divide.Details, "assert 1 / 0 == 0");
        Assert.AreEqual(0.003, divide.Seconds, 1e-9);
        CollectionAssert.AreEqual(new[] { "test_divide", "test_write" }, TestRerun.FailedNames(report, RerunBy.Case).ToList());
    }

    [TestMethod]
    public void GtestReportHasSuitesCountsAndFailureMessages()
    {
        var report = JUnitReader.Read(Fixtures.Path("junit", "gtest.xml"));

        CollectionAssert.AreEqual(new[] { "MathTest", "StringTest", "ParserTest" }, report.Suites.Select(s => s.Name).ToList());
        Assert.AreEqual((3, 2, 1), (report.Count(TestOutcome.Passed), report.Count(TestOutcome.Failed), report.Count(TestOutcome.Skipped)));
        StringAssert.StartsWith(report.Cases.Single(c => c.Name == "Divides").Message, "math_test.cpp:12\nExpected equality");
        CollectionAssert.AreEqual(new[] { "MathTest", "ParserTest" }, TestRerun.FailedNames(report, RerunBy.Suite).ToList());
    }

    [TestMethod]
    public void TestReportLoadsFromAStringOrAnObjectAndAPlainOneWritesBackAsAString()
    {
        var plain = JsonSerializer.Deserialize<ScriptNode>("""{ "path": "t.bat", "testReport": "build/junit.xml" }""", ConfigJson.Options)!;
        var full = JsonSerializer.Deserialize<ScriptNode>(
            """{ "path": "t.bat", "testReport": { "path": "build/junit.xml", "rerunParam": "match", "rerunBy": "suite" } }""", ConfigJson.Options)!;

        Assert.AreEqual("build/junit.xml", plain.TestReport!.Path);
        Assert.IsNull(plain.TestReport.RerunParam);
        Assert.AreEqual(("build/junit.xml", "match", RerunBy.Suite), (full.TestReport!.Path, full.TestReport.RerunParam, full.TestReport.RerunBy));
        Assert.AreEqual("\"build/junit.xml\"", JsonSerializer.SerializeToNode(plain, ConfigJson.Options)!["testReport"]!.ToJsonString());
        Assert.AreEqual("suite", JsonSerializer.SerializeToNode(full, ConfigJson.Options)!["testReport"]!["rerunBy"]!.GetValue<string>());
    }
}
