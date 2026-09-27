using System.Globalization;
using System.Text.Json.Nodes;
using BatchPad.Core.Output;

namespace BatchPad.Core.History;

public enum BenchmarkVerdict { Slower, Faster, Unchanged, New, Gone }

/// <summary>One benchmark in both runs; a time is null where the benchmark is missing from that run.</summary>
public sealed record BenchmarkChange(string Name, double? BaselineNs, double? CurrentNs, double? ChangePercent, BenchmarkVerdict Verdict);

/// <summary>A run's benchmarks against a baseline run's, by real time or CPU time.</summary>
public sealed record BenchmarkComparison(string RunId, string? BaselineRunId, DateTimeOffset? BaselineStartedAt, bool Cpu, double Threshold,
    List<BenchmarkChange> Benchmarks)
{
    private const double Tolerance = 1e-9;

    public bool Regressed => Benchmarks.Any(b => b.Verdict == BenchmarkVerdict.Slower);

    /// <summary>The changes, slowest first, then new and gone benchmarks.</summary>
    public static List<BenchmarkChange> Compare(IReadOnlyList<BenchmarkResult>? baseline, IReadOnlyList<BenchmarkResult> current, double threshold,
        bool cpu = false)
    {
        var before = new Dictionary<string, BenchmarkResult>(StringComparer.Ordinal);
        foreach (var result in baseline ?? [])
            before.TryAdd(result.Name, result);
        var changes = new List<BenchmarkChange>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in current)
        {
            if (!names.Add(result.Name))
                continue;
            var now = cpu ? result.CpuNs : result.RealNs;
            if (!before.TryGetValue(result.Name, out var old))
            {
                changes.Add(new BenchmarkChange(result.Name, null, now, null, BenchmarkVerdict.New));
                continue;
            }
            var then = cpu ? old.CpuNs : old.RealNs;
            double? change = then > 0 ? (now - then) / then * 100 : null;
            var verdict = change > threshold + Tolerance ? BenchmarkVerdict.Slower
                : change < -threshold - Tolerance ? BenchmarkVerdict.Faster
                : BenchmarkVerdict.Unchanged;
            changes.Add(new BenchmarkChange(result.Name, then, now, change, verdict));
        }
        changes.AddRange(before.Values.Where(b => !names.Contains(b.Name))
            .Select(b => new BenchmarkChange(b.Name, cpu ? b.CpuNs : b.RealNs, null, null, BenchmarkVerdict.Gone)));
        return [.. changes.OrderBy(c => c.ChangePercent is null).ThenByDescending(c => c.ChangePercent).ThenBy(c => c.Verdict)
            .ThenBy(c => c.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Compares with <paramref name="baseline"/> at the current run's threshold; with no baseline every benchmark is new.
    /// </summary>
    public static BenchmarkComparison Of(RunRecord current, RunRecord? baseline, bool cpu = false)
    {
        var threshold = current.Benchmarks?.Threshold ?? 0;
        return new BenchmarkComparison(current.Id, baseline?.Id, baseline?.StartedAt, cpu, threshold,
            Compare(baseline?.Benchmarks?.Results, current.Benchmarks?.Results ?? [], threshold, cpu));
    }

    /// <summary>
    /// The latest earlier successful run of the same node with the same values and benchmarks, so a run is compared only
    /// with one that measured the same thing.
    /// </summary>
    public static RunRecord? DefaultBaseline(RunRecord record, IEnumerable<RunRecord> history) =>
        Baselines(record, history).FirstOrDefault(r => SuitsAsDefault(record, r));

    /// <summary>Whether <paramref name="baseline"/>, one of <see cref="Baselines"/>, may be the default for <paramref name="record"/>.</summary>
    public static bool SuitsAsDefault(RunRecord record, RunRecord baseline) => baseline.Succeeded && SameValues(baseline.Values, record.Values);

    /// <summary>Earlier runs of the same node that recorded benchmarks, newest first.</summary>
    public static IEnumerable<RunRecord> Baselines(RunRecord record, IEnumerable<RunRecord> history) =>
        history.Where(r => r.Id != record.Id && r.NodeKey == record.NodeKey && r.StartedAt < record.StartedAt
                && r.Benchmarks is { Results.Count: > 0 })
            .OrderByDescending(r => r.StartedAt);

    private static bool SameValues(Dictionary<string, JsonNode?> a, Dictionary<string, JsonNode?> b) =>
        a.Count == b.Count && a.All(pair => b.TryGetValue(pair.Key, out var other) && JsonNode.DeepEquals(pair.Value, other));

    public static string FormatTime(double? ns) => ns switch
    {
        null => "-",
        >= 1e9 => Format(ns.Value / 1e9, "s"),
        >= 1e6 => Format(ns.Value / 1e6, "ms"),
        >= 1e3 => Format(ns.Value / 1e3, "us"),
        _ => Format(ns.Value, "ns"),
    };

    public static string FormatChange(double? percent) =>
        percent is { } value ? value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%" : "";

    private static string Format(double value, string unit) =>
        value.ToString(value >= 100 ? "0" : value >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + unit;
}
