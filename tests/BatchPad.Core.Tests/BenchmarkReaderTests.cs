using System.Text.Json;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Output;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class BenchmarkReaderTests
{
    [TestMethod]
    public void MeanAndMedianReplaceTheirRepetitionsAndTimesAreInNanoseconds()
    {
        var results = BenchmarkReader.Read(Fixtures.Path("benchmarks", "google.json"));

        CollectionAssert.AreEqual(new[] { "BM_Parse", "BM_Sort/1024/mean", "BM_Sort/1024/median", "BM_Io", "BM_Grow/8" },
            results.Select(r => r.Name).ToList());
        Assert.AreEqual(new BenchmarkResult("BM_Parse", 1250.5, 1240.0, 50000), results[0]);
        Assert.AreEqual(2.2e6, results[1].RealNs, 1e-3);
        Assert.AreEqual(2.1e6, results[1].CpuNs, 1e-3);
        Assert.AreEqual(3, results[1].Iterations);
        Assert.AreEqual((3500, 250), (results[3].RealNs, results[3].CpuNs));
    }

    [TestMethod]
    public void BareNaNAndInfinityCountersDoNotLoseTheReport()
    {
        var results = BenchmarkReader.Read(Fixtures.Path("benchmarks", "nan.json"));

        Assert.AreEqual(new BenchmarkResult("BM_Ratio", 42, 41, 100), results.Single());
    }

    [TestMethod]
    public void AStaleMalformedOrNonGoogleReportIsNotRead()
    {
        using var workspace = new RunWorkspace("""
            { "id": "bench", "path": "bench.bat", "benchmarkReport": "out.json" },
            { "id": "other", "path": "bench.bat", "benchmarkReport": { "path": "out.json", "format": "nanobench" } }
            """);
        var report = workspace.Temp.Path("out.json");
        var startedAt = DateTimeOffset.UtcNow;
        File.WriteAllText(report, File.ReadAllText(Fixtures.Path("benchmarks", "google.json")));

        Assert.HasCount(5, BenchmarkReportReader.ForFinishedRun(workspace.Request("bench"), startedAt)!);
        Assert.IsNull(BenchmarkReportReader.ForFinishedRun(workspace.Request("other"), startedAt));

        File.SetLastWriteTimeUtc(report, DateTime.UtcNow.AddHours(-1));
        Assert.IsNull(BenchmarkReportReader.ForFinishedRun(workspace.Request("bench"), startedAt));

        File.WriteAllText(report, """{ "benchmarks": [ { "name": """);
        Assert.IsNull(BenchmarkReportReader.ForFinishedRun(workspace.Request("bench"), startedAt));
    }

    [TestMethod]
    public void AFileWithoutBenchmarksIsRejected() =>
        Assert.ThrowsExactly<JsonException>(() => BenchmarkReader.Read(JsonDocument.Parse("""{ "context": {} }""").RootElement));

    [TestMethod]
    public void BenchmarkReportLoadsFromAStringOrAnObjectAndAPlainOneWritesBackAsAString()
    {
        var plain = JsonSerializer.Deserialize<ScriptNode>("""{ "path": "b.bat", "benchmarkReport": "out/bench.json" }""", ConfigJson.Options)!;
        var full = JsonSerializer.Deserialize<ScriptNode>(
            """{ "path": "b.bat", "benchmarkReport": { "path": "out/bench.json", "threshold": 10 } }""", ConfigJson.Options)!;

        Assert.AreEqual("out/bench.json", plain.BenchmarkReport!.Path);
        Assert.AreEqual(("out/bench.json", 10.0), (full.BenchmarkReport!.Path, full.BenchmarkReport.Threshold));
        Assert.AreEqual("\"out/bench.json\"", JsonSerializer.SerializeToNode(plain, ConfigJson.Options)!["benchmarkReport"]!.ToJsonString());
        Assert.AreEqual(10, JsonSerializer.SerializeToNode(full, ConfigJson.Options)!["benchmarkReport"]!["threshold"]!.GetValue<double>());
    }
}
