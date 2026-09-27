using System.Globalization;
using BatchPad.Core.History;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchPad.App.ViewModels;

/// <summary>A run's benchmarks against a baseline run, which the user can change to any earlier run of the same node.</summary>
public sealed partial class BenchmarkResultsViewModel : ObservableObject
{
    private readonly RunRecord _record;

    public BenchmarkResultsViewModel(RunRecord record, IEnumerable<RunRecord> history)
    {
        _record = record;
        Baselines = [.. BenchmarkComparison.Baselines(record, history).Select(r => new BaselineOption(r))];
        baseline = Baselines.FirstOrDefault(b => BenchmarkComparison.SuitsAsDefault(record, b.Record));
        rows = Compare();
    }

    public IReadOnlyList<BaselineOption> Baselines { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header))]
    private BaselineOption? baseline;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private IReadOnlyList<BenchmarkRowViewModel> rows;

    public string Header => Baseline is { } chosen ? $"vs {chosen.WhenText}" : "No earlier run to compare with";

    public string Summary
    {
        get
        {
            var slower = Rows.Count(r => r.Verdict == BenchmarkVerdict.Slower);
            var faster = Rows.Count(r => r.Verdict == BenchmarkVerdict.Faster);
            var text = Rows.Count == 1 ? "1 benchmark" : $"{Rows.Count} benchmarks";
            return (slower, faster) switch
            {
                (0, 0) => text,
                (_, 0) => $"{text} · {slower} slower",
                (0, _) => $"{text} · {faster} faster",
                _ => $"{text} · {slower} slower · {faster} faster",
            };
        }
    }

    partial void OnBaselineChanged(BaselineOption? value) => Rows = Compare();

    private IReadOnlyList<BenchmarkRowViewModel> Compare() =>
        [.. BenchmarkComparison.Of(_record, Baseline?.Record).Benchmarks.Select(c => new BenchmarkRowViewModel(c))];
}

public sealed class BaselineOption(RunRecord record)
{
    public RunRecord Record { get; } = record;
    public string WhenText => Record.StartedAt.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture);
    public string Label => $"{WhenText} · {RunViewModel.StatusOf(Record)}";
}

public sealed class BenchmarkRowViewModel(BenchmarkChange change)
{
    public string Name => change.Name;
    public BenchmarkVerdict Verdict => change.Verdict;
    public string TimeText => BenchmarkComparison.FormatTime(change.CurrentNs);
    public string BaselineText => BenchmarkComparison.FormatTime(change.BaselineNs);

    public string ChangeText => Verdict switch
    {
        BenchmarkVerdict.New => "new",
        BenchmarkVerdict.Gone => "gone",
        _ => BenchmarkComparison.FormatChange(change.ChangePercent),
    };
}

/// <summary>A recorded run's benchmarks opened from History.</summary>
public sealed class BenchmarkTabViewModel(RunRecord record, NodeViewModel? node, BenchmarkResultsViewModel benchmarks)
    : OutputTabViewModel($"{record.Name} benchmarks", node)
{
    public BenchmarkResultsViewModel Benchmarks { get; } = benchmarks;
    public override bool IsRunning => false;
    public override bool Succeeded => record.Succeeded;
    public override string StatusText => RunViewModel.StatusOf(record);
    public override string DurationText => FormatDuration(record.Duration);
    protected override Task StopRunAsync() => Task.CompletedTask;
}
