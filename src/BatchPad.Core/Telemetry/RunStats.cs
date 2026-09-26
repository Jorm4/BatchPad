using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Running;

namespace BatchPad.Core.Telemetry;

public static class TriggerClasses
{
    public const string You = "you";
    public const string Agents = "agents";
    public const string Schedules = "schedules";
    public const string Cli = "cli";

    public static string Of(string trigger) =>
        trigger.StartsWith("agent:", StringComparison.Ordinal) ? Agents
        : RunTriggers.ScheduleKey(trigger) is not null ? Schedules
        : trigger == RunTriggers.Cli ? Cli
        : You;
}

/// <param name="Trend">The median of the later half of the runs against the earlier half: 0.25 is 25% slower; null under four runs.</param>
public sealed record ScriptStats(string NodeKey, string Name, string? Folder, int Runs, int Failures, double FailureRate, double TotalSeconds,
    double MedianSeconds, double P95Seconds, double? Trend, DateTimeOffset LastRun);

/// <param name="Share">The fraction of <see cref="RunStats.TotalSeconds"/>; a run with several tags counts under each.</param>
public sealed record TimeShare(string Name, int Runs, double Seconds, double Share);

public sealed record RepeatedRun(string NodeKey, string Name, Dictionary<string, JsonNode?> Values, string? ExtraArguments,
    int Count, DateTimeOffset First, DateTimeOffset Last);

public sealed record SlowTest(string Name, string NodeKey, string Script, double Seconds);

/// <param name="Passes">Complete test reports of the script, after its first failure, that don't list it as failed.</param>
public sealed record FlakyTest(string Name, string NodeKey, string Script, int Failures, int Passes);

/// <summary>
/// The Insights figures for the runs in a period (§4.4). A workflow's time is counted once: a step whose workflow record is
/// in the period is left out of <see cref="TotalSeconds"/>, the folder, tag, trigger and checkout shares, and repeats, though it
/// still counts as a run of its own script.
/// </summary>
public sealed record RunStats(DateTimeOffset Since, DateTimeOffset Until, int Runs, double TotalSeconds,
    List<ScriptStats> Scripts, List<TimeShare> Folders, List<TimeShare> Tags, List<TimeShare> Triggers,
    List<RepeatedRun> Repeats, List<SlowTest> SlowestTests, List<FlakyTest> FlakyTests, List<TimeShare> Checkouts)
{
    public const int RepeatCount = 3;
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(30);
    public const int MaxSlowestTests = 10;

    public static RunStats Compute(IEnumerable<RunRecord> records, TimeSpan period, DateTimeOffset now)
    {
        var since = now - period;
        var inPeriod = records.Where(r => r.StartedAt >= since && r.StartedAt <= now).OrderBy(r => r.StartedAt).ToList();
        var ids = inPeriod.Select(r => r.Id).ToHashSet();
        var counted = inPeriod.Where(r => r.ParentRunId is null || !ids.Contains(r.ParentRunId)).ToList();
        var total = counted.Sum(r => r.Duration.TotalSeconds);

        return new RunStats(since, now, inPeriod.Count, total,
            [.. inPeriod.GroupBy(r => r.NodeKey).Select(ScriptStatsOf).OrderByDescending(s => s.TotalSeconds).ThenBy(s => s.Name)],
            Shares(counted, r => [r.Folder ?? ""], total),
            Shares(counted, r => r.Tags ?? [], total),
            Shares(counted, r => [TriggerClasses.Of(r.Trigger)], total),
            FindRepeats(counted),
            FindSlowestTests(inPeriod),
            FindFlakyTests(inPeriod),
            Shares(counted, r => [r.Checkout?.Name ?? ""], total));
    }

    /// <summary>Reads a period such as <c>1d</c>, <c>7d</c>, <c>30d</c> or <c>12h</c>.</summary>
    public static bool TryParsePeriod(string? text, out TimeSpan period)
    {
        period = default;
        if (text is not { Length: >= 2 } || !int.TryParse(text[..^1], out var count) || count <= 0)
            return false;
        period = char.ToLowerInvariant(text[^1]) switch
        {
            'd' => TimeSpan.FromDays(count),
            'h' => TimeSpan.FromHours(count),
            _ => default,
        };
        return period != default;
    }

    public static double Median(IReadOnlyList<double> sorted) =>
        sorted.Count == 0 ? 0
        : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2]
        : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;

    /// <summary>The nearest-rank percentile.</summary>
    public static double Percentile(IReadOnlyList<double> sorted, double fraction) =>
        sorted.Count == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(fraction * sorted.Count) - 1)];

    private static ScriptStats ScriptStatsOf(IGrouping<string, RunRecord> runs)
    {
        var inOrder = runs.ToList();
        var latest = inOrder[^1];
        var sorted = inOrder.Select(r => r.Duration.TotalSeconds).Order().ToList();
        var failures = inOrder.Count(r => r.Outcome != RunOutcome.Stopped && !r.Succeeded);
        return new ScriptStats(runs.Key, latest.Name, latest.Folder, inOrder.Count, failures, (double)failures / inOrder.Count, sorted.Sum(),
            Median(sorted), Percentile(sorted, 0.95), Trend(inOrder), latest.StartedAt);
    }

    private static double? Trend(List<RunRecord> inOrder)
    {
        if (inOrder.Count < 4)
            return null;
        var half = inOrder.Count / 2;
        var earlier = Median([.. inOrder.Take(half).Select(r => r.Duration.TotalSeconds).Order()]);
        var later = Median([.. inOrder.TakeLast(half).Select(r => r.Duration.TotalSeconds).Order()]);
        return earlier > 0 ? later / earlier - 1 : null;
    }

    private static List<TimeShare> Shares(List<RunRecord> counted, Func<RunRecord, IEnumerable<string>> keysOf, double total) =>
    [
        .. counted.SelectMany(r => keysOf(r).Distinct().Select(key => (key, r)))
            .GroupBy(p => p.key, p => p.r)
            .Select(g =>
            {
                var seconds = g.Sum(r => r.Duration.TotalSeconds);
                return new TimeShare(g.Key, g.Count(), seconds, total > 0 ? seconds / total : 0);
            })
            .OrderByDescending(s => s.Seconds).ThenBy(s => s.Name, StringComparer.Ordinal),
    ];

    private static List<RepeatedRun> FindRepeats(List<RunRecord> counted)
    {
        var repeats = new List<RepeatedRun>();
        foreach (var same in counted.GroupBy(r => (r.NodeKey, ValuesKey(r), r.ExtraArguments ?? "")))
        {
            var runs = same.ToList();
            var (bestStart, bestCount) = (0, 0);
            for (int start = 0, end = 0; start < runs.Count; start++)
            {
                while (end < runs.Count && runs[end].StartedAt - runs[start].StartedAt <= RepeatWindow)
                    end++;
                if (end - start > bestCount)
                    (bestStart, bestCount) = (start, end - start);
            }
            if (bestCount < RepeatCount)
                continue;
            var first = runs[bestStart];
            repeats.Add(new RepeatedRun(first.NodeKey, runs[^1].Name, first.Values, first.ExtraArguments, bestCount,
                first.StartedAt, runs[bestStart + bestCount - 1].StartedAt));
        }
        return [.. repeats.OrderByDescending(r => r.Count).ThenByDescending(r => r.Last)];
    }

    private static string ValuesKey(RunRecord record) =>
        string.Join('\n', record.Values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value?.ToJsonString()}"));

    private static List<SlowTest> FindSlowestTests(List<RunRecord> inPeriod) =>
    [
        .. inPeriod.Where(r => r.Tests is not null)
            .SelectMany(r => r.Tests!.Slowest.Select(t => (Run: r, Test: t)))
            .GroupBy(p => (p.Run.NodeKey, p.Test.Name))
            .Select(g => new SlowTest(g.Key.Name, g.Key.NodeKey, g.Last().Run.Name, g.Max(p => p.Test.Seconds)))
            .OrderByDescending(t => t.Seconds).ThenBy(t => t.Name, StringComparer.Ordinal)
            .Take(MaxSlowestTests),
    ];

    private static List<FlakyTest> FindFlakyTests(List<RunRecord> inPeriod)
    {
        var flaky = new List<FlakyTest>();
        foreach (var script in inPeriod.Where(r => r.Tests is not null).GroupBy(r => r.NodeKey))
        {
            var runs = script.ToList();
            var failures = new Dictionary<string, (int Failures, int Passes)>();
            foreach (var run in runs)
            {
                var tests = run.Tests!;
                var failed = tests.FailedNames.ToHashSet();
                var complete = tests.Failed <= tests.FailedNames.Count && tests.Passed > 0;
                foreach (var (name, counts) in failures.ToList())
                    if (complete && !failed.Contains(name))
                        failures[name] = counts with { Passes = counts.Passes + 1 };
                foreach (var name in failed)
                    failures[name] = failures.TryGetValue(name, out var counts) ? counts with { Failures = counts.Failures + 1 } : (1, 0);
            }
            flaky.AddRange(failures.Where(f => f.Value.Passes > 0)
                .Select(f => new FlakyTest(f.Key, script.Key, runs[^1].Name, f.Value.Failures, f.Value.Passes)));
        }
        return [.. flaky.OrderByDescending(f => f.Failures).ThenBy(f => f.Name, StringComparer.Ordinal)];
    }
}
