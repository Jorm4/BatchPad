using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

/// <param name="rerun">Re-runs with the given failed names; null hides "Re-run failed".</param>
public sealed partial class TestResultsViewModel(JUnitReport report, RerunBy rerunBy, Action<IReadOnlyList<string>>? rerun) : ObservableObject
{
    private readonly IReadOnlyList<TestSuiteRowViewModel> _all = [.. report.Suites.Select(s => new TestSuiteRowViewModel(s, failuresOnly: false))];
    private readonly IReadOnlyList<TestSuiteRowViewModel> _failures =
        [.. report.Suites.Where(s => s.Failed).Select(s => new TestSuiteRowViewModel(s, failuresOnly: true))];

    public int Passed { get; } = report.Count(TestOutcome.Passed);
    public int Failed { get; } = report.Count(TestOutcome.Failed);
    public int Skipped { get; } = report.Count(TestOutcome.Skipped);
    public string Summary => $"{Passed} passed · {Failed} failed · {Skipped} skipped";

    public IReadOnlyList<TestSuiteRowViewModel> Suites => FailuresOnly ? _failures : _all;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Suites))]
    private bool failuresOnly;

    public bool CanRerun => rerun is not null;

    public IReadOnlyList<string> FailedNames => TestRerun.FailedNames(report, rerunBy);

    [RelayCommand(CanExecute = nameof(HasFailures))]
    private void RerunFailed() => rerun?.Invoke(FailedNames);

    private bool HasFailures() => Failed > 0;
}

public sealed class TestSuiteRowViewModel(TestSuiteResult suite, bool failuresOnly)
{
    public string Name => suite.Name;
    public bool Failed => suite.Failed;
    public string Glyph => Failed ? "✗" : "✓";
    public string TimeText => OutputTabViewModel.FormatDuration(TimeSpan.FromSeconds(suite.Seconds));

    public IReadOnlyList<TestCaseRowViewModel> Cases { get; } =
        [.. suite.Cases.Where(c => !failuresOnly || c.Outcome == TestOutcome.Failed).Select(c => new TestCaseRowViewModel(c))];
}

public sealed class TestCaseRowViewModel(TestCaseResult result)
{
    public string Name => result.Name;
    public TestOutcome Outcome => result.Outcome;
    public string Glyph => Outcome switch { TestOutcome.Failed => "✗", TestOutcome.Skipped => "–", _ => "✓" };
    public string TimeText => OutputTabViewModel.FormatDuration(TimeSpan.FromSeconds(result.Seconds));
    public string? Message => Outcome == TestOutcome.Failed ? result.Message : null;
    public string? Details => result.Details;
}
