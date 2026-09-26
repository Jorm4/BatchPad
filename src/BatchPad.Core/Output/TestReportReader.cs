using System.Xml;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Output;

/// <summary>Reads the JUnit report a finished run wrote, if it has one (§5).</summary>
public static class TestReportReader
{
    private static readonly TimeSpan ClockSlack = TimeSpan.FromSeconds(2);

    /// <summary>The report at the script's <c>testReport.path</c>, unless it is older than the run and so left over from an earlier one.</summary>
    public static JUnitReport? ForFinishedRun(RunRequest request, DateTimeOffset startedAt)
    {
        try
        {
            return TestRerun.ReportPath(request) is { } path && File.Exists(path)
                && File.GetLastWriteTimeUtc(path) >= startedAt.UtcDateTime - ClockSlack
                ? JUnitReader.Read(path)
                : null;
        }
        catch (Exception ex) when (ex is TemplateException or XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
