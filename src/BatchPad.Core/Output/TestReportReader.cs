using BatchPad.Core.Running;

namespace BatchPad.Core.Output;

/// <summary>Reads the JUnit report a finished run wrote, if it has one (§5).</summary>
public static class TestReportReader
{
    /// <summary>The report at the script's <c>testReport.path</c>, unless it is older than the run and so left over from an earlier one.</summary>
    public static JUnitReport? ForFinishedRun(RunRequest request, DateTimeOffset startedAt) =>
        RunReportFile.Read(request.Script.TestReport?.Path, request, startedAt, JUnitReader.Read);
}
