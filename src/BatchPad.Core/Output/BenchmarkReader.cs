using System.Text.Json;
using System.Text.RegularExpressions;

namespace BatchPad.Core.Output;

public sealed record BenchmarkResult(string Name, double RealNs, double CpuNs, long Iterations);

/// <summary>Reads Google Benchmark JSON as <c>--benchmark_out_format=json</c> writes it, with times in nanoseconds.</summary>
public static partial class BenchmarkReader
{
    /// <exception cref="JsonException">The file isn't Google Benchmark JSON.</exception>
    public static IReadOnlyList<BenchmarkResult> Read(string path)
    {
        var json = QuotedOrNonFinite().Replace(File.ReadAllText(path), m => m.Value[0] == '"' ? m.Value : "null");
        using var document = JsonDocument.Parse(json);
        return Read(document.RootElement);
    }

    /// <summary>
    /// A benchmark run with repetitions is kept as its mean and median, named <c>name/mean</c> and <c>name/median</c>;
    /// others as their single row. Rows without a real time are skipped.
    /// </summary>
    public static IReadOnlyList<BenchmarkResult> Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("benchmarks", out var rows) || rows.ValueKind != JsonValueKind.Array)
            throw new JsonException("No 'benchmarks' array.");
        var entries = rows.EnumerateArray()
            .Where(r => r.ValueKind == JsonValueKind.Object && !Bool(r, "error_occurred") && HasNumber(r, "real_time")
                && (!IsAggregate(r) || String(r, "aggregate_name") is "mean" or "median"))
            .ToList();
        var aggregated = entries.Where(IsAggregate).Select(RunName).ToHashSet(StringComparer.Ordinal);
        var results = new List<BenchmarkResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in entries)
        {
            string name;
            if (IsAggregate(row))
                name = $"{RunName(row)}/{String(row, "aggregate_name")}";
            else if (aggregated.Contains(RunName(row)))
                continue;
            else
                name = String(row, "name") ?? "";
            if (name.Length == 0 || !seen.Add(name))
                continue;
            var scale = Nanoseconds(String(row, "time_unit"));
            results.Add(new BenchmarkResult(name, Number(row, "real_time") * scale, Number(row, "cpu_time") * scale,
                row.TryGetProperty("iterations", out var iterations) && iterations.TryGetInt64(out var count) ? count : 0));
        }
        return results;
    }

    private static bool IsAggregate(JsonElement row) => String(row, "run_type") == "aggregate";

    private static string RunName(JsonElement row) => String(row, "run_name") ?? String(row, "name") ?? "";

    private static double Nanoseconds(string? unit) => unit switch
    {
        "us" => 1e3,
        "ms" => 1e6,
        "s" => 1e9,
        _ => 1,
    };

    private static string? String(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasNumber(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number;

    private static double Number(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static bool Bool(JsonElement row, string name) =>
        row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    // Google Benchmark writes NaN and infinite counters as bare tokens, which JSON doesn't allow; strings are matched to be skipped.
    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""|-?(?:NaN|Infinity)")]
    private static partial Regex QuotedOrNonFinite();
}
