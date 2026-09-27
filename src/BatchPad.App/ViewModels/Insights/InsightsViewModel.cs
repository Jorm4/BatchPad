using System.Collections.Specialized;
using BatchPad.Core.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Insights;

public sealed record TimeBar(string Name, int Runs, double Share, string DurationText)
{
    public const double FullWidth = 240;
    public double BarWidth => Math.Max(1, Share * FullWidth);
    public string ShareText => InsightsViewModel.Percent(Share);
}

public sealed record ScriptRow(ScriptStats Stats)
{
    public string NodeKey => Stats.NodeKey;
    public string Name => Stats.Name;
    public string RunsText => Stats.Runs.ToString();
    public string MedianText => InsightsViewModel.Seconds(Stats.MedianSeconds);
    public string P95Text => InsightsViewModel.Seconds(Stats.P95Seconds);
    public string TotalText => InsightsViewModel.Seconds(Stats.TotalSeconds);
    public string FailureRateText => InsightsViewModel.Percent(Stats.FailureRate);
    public bool HasFailures => Stats.Failures > 0;

    /// <summary>Changes within 10% either way show as steady.</summary>
    public string TrendText =>
        Stats.Trend is not { } trend ? ""
        : trend > 0.1 ? $"▲ {InsightsViewModel.Percent(trend)}"
        : trend < -0.1 ? $"▼ {InsightsViewModel.Percent(-trend)}"
        : "→";
}

public sealed record RepeatRow(RepeatedRun Repeat)
{
    public string NodeKey => Repeat.NodeKey;
    public string Name => Repeat.Name;
    public string ValuesText => string.Join(" ", Repeat.Values.Select(v => $"{v.Key}={v.Value?.ToJsonString()}").Append(Repeat.ExtraArguments ?? "")).Trim();
    public string CountText => $"{Repeat.Count}× in {(int)Math.Ceiling((Repeat.Last - Repeat.First).TotalMinutes)} min";
}

public sealed record TestRow(string NodeKey, string Name, string Script, string Detail);

public enum ScriptSort { Name, Runs, Median, P95, Trend, FailureRate, Total }

/// <summary>Where run time went and how each script is doing, from the workspace's history (§4.4).</summary>
public sealed partial class InsightsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private bool _refreshQueued;

    public InsightsViewModel(MainViewModel main)
    {
        _main = main;
        _main.History.Runs.CollectionChanged += OnHistoryChanged;
        Refresh();
    }

    public static IReadOnlyList<string> Periods { get; } = ["1d", "7d", "30d"];

    [ObservableProperty]
    private string period = "7d";

    [ObservableProperty]
    private RunStats? stats;

    [ObservableProperty]
    private ScriptSort sortBy = ScriptSort.Total;

    [ObservableProperty]
    private bool sortDescending = true;

    public string TotalText => Stats is { } s ? $"{Seconds(s.TotalSeconds)} in {s.Runs} runs" : "";
    public bool IsEmpty => Stats is null || Stats.Runs == 0;

    public IReadOnlyList<TimeBar> Folders => Bars(Stats?.Folders, name => name.Length == 0 ? "(top level)" : name);
    public IReadOnlyList<TimeBar> Tags => Bars(Stats?.Tags, name => name);
    public IReadOnlyList<TimeBar> Triggers => Bars(Stats?.Triggers, TriggerName);
    public IReadOnlyList<TimeBar> Checkouts => Bars(Stats?.Checkouts, name => name.Length == 0 ? "(not a git checkout)" : name);

    public IReadOnlyList<ScriptRow> Scripts => Sorted(Stats?.Scripts.Select(s => new ScriptRow(s)) ?? []);
    public IReadOnlyList<RepeatRow> Repeats => [.. Stats?.Repeats.Select(r => new RepeatRow(r)) ?? []];

    public IReadOnlyList<TestRow> SlowestTests =>
        [.. Stats?.SlowestTests.Select(t => new TestRow(t.NodeKey, t.Name, t.Script, Seconds(t.Seconds))) ?? []];

    public IReadOnlyList<TestRow> FlakyTests =>
        [.. Stats?.FlakyTests.Select(t => new TestRow(t.NodeKey, t.Name, t.Script, $"failed {t.Failures}, passed {t.Passes}")) ?? []];

    public bool HasRepeats => Stats?.Repeats.Count > 0;
    public bool HasSlowestTests => Stats?.SlowestTests.Count > 0;
    public bool HasFlakyTests => Stats?.FlakyTests.Count > 0;

    public event Action? Closed;

    public void Refresh()
    {
        _refreshQueued = false;
        RunStats.TryParsePeriod(Period, out var span);
        Stats = RunStats.Compute(_main.History.Store?.Recent() ?? [], span, _main.Time.GetUtcNow());
    }

    public void Detach() => _main.History.Runs.CollectionChanged -= OnHistoryChanged;

    partial void OnPeriodChanged(string value) => Refresh();

    partial void OnStatsChanged(RunStats? value) => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Sort(ScriptSort column)
    {
        if (SortBy == column)
            SortDescending = !SortDescending;
        else
            (SortBy, SortDescending) = (column, column != ScriptSort.Name);
        OnPropertyChanged(nameof(Scripts));
    }

    [RelayCommand]
    private void SelectScript(string? nodeKey)
    {
        if (nodeKey is not null && _main.Tree?.AllNodes.FirstOrDefault(n => n.Key == nodeKey) is { } node)
            _main.KeepingPageOpen(node.Reveal);
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshQueued)
            return;
        _refreshQueued = true;
        _main.Services.Dispatcher.Post(Refresh);
    }

    private List<ScriptRow> Sorted(IEnumerable<ScriptRow> rows)
    {
        Func<ScriptRow, object> key = SortBy switch
        {
            ScriptSort.Name => r => r.Name,
            ScriptSort.Runs => r => r.Stats.Runs,
            ScriptSort.Median => r => r.Stats.MedianSeconds,
            ScriptSort.P95 => r => r.Stats.P95Seconds,
            ScriptSort.Trend => r => r.Stats.Trend ?? double.MinValue,
            ScriptSort.FailureRate => r => r.Stats.FailureRate,
            _ => r => r.Stats.TotalSeconds,
        };
        return [.. SortDescending ? rows.OrderByDescending(key) : rows.OrderBy(key)];
    }

    private static List<TimeBar> Bars(IEnumerable<TimeShare>? shares, Func<string, string> label) =>
        [.. shares?.Select(s => new TimeBar(label(s.Name), s.Runs, s.Share, Seconds(s.Seconds))) ?? []];

    private static string TriggerName(string triggerClass) => triggerClass switch
    {
        TriggerClasses.You => "You",
        TriggerClasses.Agents => "Agents",
        TriggerClasses.Schedules => "Schedules",
        TriggerClasses.Cli => "Command line",
        _ => triggerClass,
    };

    internal static string Seconds(double seconds) => OutputTabViewModel.FormatDuration(TimeSpan.FromSeconds(seconds));

    internal static string Percent(double fraction) => $"{Math.Round(fraction * 100):0}%";
}
