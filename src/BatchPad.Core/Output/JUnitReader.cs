using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BatchPad.Core.Output;

public enum TestOutcome { Passed, Failed, Skipped }

public sealed record TestCaseResult(string Suite, string ClassName, string Name, double Seconds, TestOutcome Outcome, string? Message, string? Details);

public sealed record TestSuiteResult(string Name, double Seconds, IReadOnlyList<TestCaseResult> Cases)
{
    public bool Failed => Cases.Any(c => c.Outcome == TestOutcome.Failed);
}

public sealed record JUnitReport(IReadOnlyList<TestSuiteResult> Suites)
{
    public IEnumerable<TestCaseResult> Cases => Suites.SelectMany(s => s.Cases);
    public int Count(TestOutcome outcome) => Cases.Count(c => c.Outcome == outcome);
}

/// <summary>Reads JUnit XML as pytest <c>--junitxml</c> and gtest <c>--gtest_output=xml</c> write it (§5).</summary>
public static class JUnitReader
{
    /// <exception cref="XmlException">The file isn't well-formed XML.</exception>
    public static JUnitReport Read(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        return Read(XDocument.Load(reader));
    }

    public static JUnitReport Read(XDocument document) =>
        new([.. document.Descendants("testsuite")
            .Where(suite => suite.Elements("testcase").Any())
            .Select(ReadSuite)]);

    private static TestSuiteResult ReadSuite(XElement suite)
    {
        var name = (string?)suite.Attribute("name") ?? "";
        return new TestSuiteResult(name, Seconds(suite), [.. suite.Elements("testcase").Select(c => ReadCase(c, name))]);
    }

    private static TestCaseResult ReadCase(XElement testCase, string suite)
    {
        var failure = testCase.Element("failure") ?? testCase.Element("error");
        var skipped = testCase.Element("skipped");
        var outcome = failure is not null ? TestOutcome.Failed
            : skipped is not null || (string?)testCase.Attribute("status") == "notrun" ? TestOutcome.Skipped
            : TestOutcome.Passed;
        var detail = failure ?? skipped;
        var text = detail?.Value is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        return new TestCaseResult(suite, (string?)testCase.Attribute("classname") ?? suite, (string?)testCase.Attribute("name") ?? "",
            Seconds(testCase), outcome, (string?)detail?.Attribute("message") ?? text, text);
    }

    private static double Seconds(XElement element) =>
        double.TryParse((string?)element.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : 0;
}
