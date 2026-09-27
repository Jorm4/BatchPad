using System.Globalization;
using System.Text.Json;
using BatchPad.Core.History;
using BatchPad.Core.Running;

namespace BatchPad.App.Cli;

/// <summary><c>batchpad compare</c>: a run's benchmarks against a baseline run's.</summary>
public static class CliCompare
{
    public const int Regression = 3;

    public static int Run(CliCommand command, HistoryStore store, TextWriter output, TextWriter error)
    {
        if (store.Find(command.Target!) is not { } current)
        {
            error.WriteLine($"No run '{command.Target}' in this workspace's history.");
            return CliRunner.UsageError;
        }
        if (current.Benchmarks is not { Results.Count: > 0 })
        {
            error.WriteLine($"Run '{current.Id}' recorded no benchmarks; set the script's benchmarkReport.");
            return CliRunner.Failure;
        }
        RunRecord? baseline;
        if (command.Baseline is { } baselineId)
        {
            baseline = store.Find(baselineId);
            if (baseline is null)
            {
                error.WriteLine($"No run '{baselineId}' in this workspace's history.");
                return CliRunner.UsageError;
            }
            if (baseline.Benchmarks is not { Results.Count: > 0 })
            {
                error.WriteLine($"Run '{baseline.Id}' recorded no benchmarks to compare with.");
                return CliRunner.Failure;
            }
        }
        else
            baseline = BenchmarkComparison.DefaultBaseline(current, store.Recent());

        var comparison = BenchmarkComparison.Of(current, baseline, command.Cpu);
        if (command.Json)
            output.WriteLine(JsonSerializer.Serialize(comparison, RunResultJson.Options));
        else
            Write(comparison, baseline, output);
        return comparison.Regressed ? Regression : 0;
    }

    public static void Write(BenchmarkComparison comparison, RunRecord? baseline, TextWriter output)
    {
        var time = comparison.Cpu ? "CPU time" : "real time";
        output.WriteLine(baseline is null
            ? $"No earlier run of the same script and values to compare with; {time}."
            : $"{time} vs {baseline.Id} ({baseline.StartedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture)}), "
                + $"threshold {comparison.Threshold.ToString("0.#", CultureInfo.InvariantCulture)}%");
        var width = Math.Max(9, comparison.Benchmarks.Max(b => b.Name.Length));
        output.WriteLine();
        output.WriteLine($"{"Benchmark".PadRight(width)}  {"Baseline",10}  {"Current",10}  {"Change",8}  Verdict");
        foreach (var change in comparison.Benchmarks)
            output.WriteLine($"{change.Name.PadRight(width)}  {BenchmarkComparison.FormatTime(change.BaselineNs),10}  "
                + $"{BenchmarkComparison.FormatTime(change.CurrentNs),10}  {BenchmarkComparison.FormatChange(change.ChangePercent),8}  "
                + change.Verdict.ToString().ToLowerInvariant());
    }
}
