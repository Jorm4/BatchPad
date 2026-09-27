using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.Core.Output;

/// <summary>Reads the benchmark report a finished run wrote, if it has one.</summary>
public static class BenchmarkReportReader
{
    /// <summary>The results at the script's <c>benchmarkReport.path</c>, unless the file is older than the run.</summary>
    public static IReadOnlyList<BenchmarkResult>? ForFinishedRun(RunRequest request, DateTimeOffset startedAt) =>
        request.Script.BenchmarkReport is { } report && (report.Format ?? BenchmarkReportDefinition.GoogleFormat) == BenchmarkReportDefinition.GoogleFormat
            ? RunReportFile.Read(report.Path, request, startedAt, BenchmarkReader.Read)
            : null;
}
