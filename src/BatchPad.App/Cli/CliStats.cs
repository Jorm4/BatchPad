using System.Globalization;
using BatchPad.Core.Telemetry;

namespace BatchPad.App.Cli;

public static class CliStatsFormatter
{
    public const int MaxScripts = 20;

    public static void Write(RunStats stats, TextWriter output)
    {
        output.WriteLine($"Last {Period(stats.Until - stats.Since)}: {stats.Runs} runs, {Duration(stats.TotalSeconds)} in total");
        if (stats.Scripts.Count > 0)
        {
            var scripts = stats.Scripts.Take(MaxScripts).ToList();
            var width = Math.Max(6, scripts.Max(s => s.Name.Length));
            output.WriteLine();
            output.WriteLine($"{"Script".PadRight(width)}  {"Runs",5}  {"Total",8}  {"Median",8}  {"p95",8}  {"Trend",6}  {"Failed",6}");
            foreach (var script in scripts)
                output.WriteLine($"{script.Name.PadRight(width)}  {script.Runs,5}  {Duration(script.TotalSeconds),8}  "
                    + $"{Duration(script.MedianSeconds),8}  {Duration(script.P95Seconds),8}  "
                    + $"{(script.Trend is { } trend ? Percent(trend, signed: true) : "-"),6}  {Percent(script.FailureRate),6}");
        }
        WriteShares(output, "By folder", stats.Folders);
        WriteShares(output, "By trigger", stats.Triggers);
        WriteList(output, $"Repeated {RunStats.RepeatCount}+ times within {RunStats.RepeatWindow.TotalMinutes:0} minutes",
            stats.Repeats.Select(r => $"{r.Name}  x{r.Count}"));
        WriteList(output, "Slowest tests", stats.SlowestTests.Select(t => $"{t.Name}  {Duration(t.Seconds)}  ({t.Script})"));
        WriteList(output, "Flaky tests", stats.FlakyTests.Select(t => $"{t.Name}  ({t.Script}: {t.Failures} failed, {t.Passes} passed later)"));
    }

    private static void WriteShares(TextWriter output, string title, List<TimeShare> shares) =>
        WriteList(output, title, shares.Select(s => $"{(s.Name.Length > 0 ? s.Name : "(top level)")}  {Percent(s.Share)}  ({Duration(s.Seconds)})"));

    private static void WriteList(TextWriter output, string title, IEnumerable<string> lines)
    {
        var header = false;
        foreach (var line in lines)
        {
            if (!header)
            {
                output.WriteLine();
                output.WriteLine(title + ":");
                header = true;
            }
            output.WriteLine("  " + line);
        }
    }

    private static string Period(TimeSpan period) =>
        period.TotalHours % 24 == 0
            ? period.TotalDays == 1 ? "day" : $"{period.TotalDays:0} days"
            : period.TotalHours == 1 ? "hour" : $"{period.TotalHours:0} hours";

    private static string Percent(double fraction, bool signed = false) =>
        (fraction * 100).ToString(signed ? "+0;-0;0" : "0", CultureInfo.InvariantCulture) + "%";

    private static string Duration(double seconds) => TimeSpan.FromSeconds(seconds) switch
    {
        { TotalHours: >= 1 } duration => $"{(int)duration.TotalHours}h {duration.Minutes:00}m",
        { TotalMinutes: >= 1 } duration => $"{duration.Minutes}m {duration.Seconds:00}s",
        _ => seconds.ToString("0.0", CultureInfo.InvariantCulture) + "s",
    };
}
