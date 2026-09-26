using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using BatchPad.App.Services;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

public sealed partial class ArtifactRowViewModel(ArtifactDefinition artifact, Action changed) : ObservableObject
{
    public static IReadOnlyList<EditorOption<ArtifactOpen>> OpenOptions { get; } =
    [
        new(ArtifactOpen.Never, "Link only"), new(ArtifactOpen.OnSuccess, "Open on success"), new(ArtifactOpen.Always, "Always open"),
    ];

    public ArtifactDefinition Artifact { get; } = artifact;

    [ObservableProperty]
    private string path = artifact.Path ?? "";

    [ObservableProperty]
    private EditorOption<ArtifactOpen> open = OpenOptions.First(o => o.Value == (artifact.Open ?? ArtifactOpen.Never));

    partial void OnPathChanged(string value)
    {
        Artifact.Path = value.Trim();
        changed();
    }

    partial void OnOpenChanged(EditorOption<ArtifactOpen> value)
    {
        Artifact.Open = value.Value == ArtifactOpen.Never ? null : value.Value;
        changed();
    }
}

/// <summary>The editor's After run tab (§5.1): ready pattern and its tester, stop companion, error patterns, artifacts and test report.</summary>
public sealed partial class AfterRunTabViewModel : ObservableObject
{
    private readonly ScriptNode _definition;
    private readonly Action _changed;
    private readonly Func<TemplateContext> _templates;
    private readonly Func<string?> _lastOutput;

    /// <param name="stopChoices">Scripts that can stop this one, by reference; the first is "None".</param>
    /// <param name="lastOutput">The script's last recorded output, for the ready-pattern tester.</param>
    public AfterRunTabViewModel(ScriptNode definition, IReadOnlyList<EditorOption<string?>> stopChoices, Action changed,
        Func<TemplateContext> templates, Func<string?> lastOutput)
    {
        _definition = definition;
        _changed = changed;
        _templates = templates;
        _lastOutput = lastOutput;
        StopChoices = new(stopChoices);
        if (definition.Stop is { } current && stopChoices.All(c => c.Value != current))
            StopChoices.Add(new EditorOption<string?>(current, current));
        readyPattern = definition.Ready?.Pattern ?? "";
        openUrl = definition.Ready?.Open ?? "";
        stop = StopChoices.First(c => c.Value == definition.Stop);
        testReport = definition.TestReport?.Path ?? "";
        UpdateRerunParams(definition.Params);
        rerunParam = RerunParamChoices.First(c => c.Value == definition.TestReport?.RerunParam);
        rerunScope = RerunScopes.First(c => c.Value == (definition.TestReport?.RerunBy ?? RerunBy.Case));
        errorPatterns = string.Join(Environment.NewLine, definition.ErrorPatterns ?? []);
        foreach (var artifact in definition.Artifacts ?? [])
            Artifacts.Add(new ArtifactRowViewModel(artifact, WriteArtifacts));
    }

    public ObservableCollection<EditorOption<string?>> StopChoices { get; }

    private static readonly IReadOnlyList<EditorOption<RerunBy>> RerunScopes =
        [new(RerunBy.Case, "Failed test cases"), new(RerunBy.Suite, "Suites with failures")];

    public IReadOnlyList<EditorOption<RerunBy>> RerunByChoices => RerunScopes;

    /// <summary>"None" plus the script's multichoice and text parameters, which "Re-run failed" can fill.</summary>
    public IReadOnlyList<EditorOption<string?>> RerunParamChoices { get; private set; } = [];

    [ObservableProperty]
    private EditorOption<string?> rerunParam;

    [ObservableProperty]
    private EditorOption<RerunBy> rerunScope;

    public void UpdateRerunParams(IEnumerable<ParameterDefinition>? parameters)
    {
        List<EditorOption<string?>> choices =
        [
            new(null, "None — no Re-run failed button"),
            .. (parameters ?? []).Where(p => p is { Name: not null, Type: ParameterType.Multichoice or ParameterType.Text })
                .Select(p => new EditorOption<string?>(p.Name, p.Label ?? p.Name!)),
        ];
        if (_definition.TestReport?.RerunParam is { } current && choices.All(c => c.Value != current))
            choices.Add(new(current, current));
        if (choices.SequenceEqual(RerunParamChoices))
            return;
        RerunParamChoices = choices;
        OnPropertyChanged(nameof(RerunParamChoices));
    }

    public void SelectStop(string reference, string label)
    {
        if (StopChoices.FirstOrDefault(c => c.Value == reference) is not { } choice)
            StopChoices.Add(choice = new EditorOption<string?>(reference, label));
        Stop = choice;
    }
    public IReadOnlyList<EditorOption<ArtifactOpen>> OpenOptions => ArtifactRowViewModel.OpenOptions;
    public ObservableCollection<ArtifactRowViewModel> Artifacts { get; } = [];

    [ObservableProperty]
    private string readyPattern;

    /// <summary>Opened when the ready pattern matches; <c>$1</c> is the pattern's first group.</summary>
    [ObservableProperty]
    private string openUrl;

    [ObservableProperty]
    private EditorOption<string?> stop;

    [ObservableProperty]
    private string testReport;

    /// <summary>One regular expression per line; matching output lines are marked as errors.</summary>
    [ObservableProperty]
    private string errorPatterns;

    [ObservableProperty]
    private string sampleOutput = "";

    [ObservableProperty]
    private string testerResult = "";

    partial void OnReadyPatternChanged(string value) => WriteReady();
    partial void OnOpenUrlChanged(string value) => WriteReady();
    partial void OnSampleOutputChanged(string value) => TestPattern();

    partial void OnErrorPatternsChanged(string value)
    {
        var patterns = value.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        _definition.ErrorPatterns = patterns.Count == 0 ? null : patterns;
        _changed();
    }

    [RelayCommand]
    private void UseLastOutput() => SampleOutput = _lastOutput() ?? "";

    private void TestPattern()
    {
        if (ReadyPattern.Length == 0 || SampleOutput.Length == 0)
        {
            TesterResult = "";
            return;
        }
        try
        {
            var pattern = new Regex(ReadyPattern, RegexOptions.None, UserPattern.Timeout);
            var match = SampleOutput.Split('\n').Select(line => pattern.Match(line.TrimEnd('\r'))).FirstOrDefault(m => m.Success);
            if (match is null)
                TesterResult = "No match";
            else
            {
                var url = ReadyWatcher.Expand(GeneralTabViewModel.NullIfEmpty(OpenUrl), match, _templates());
                TesterResult = url is null ? $"Matches: {match.Value}" : $"Matches: {match.Value}{Environment.NewLine}Opens: {url}";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException || RunProblems.IsRunProblem(ex))
        {
            TesterResult = $"⚠ {ex.Message}";
        }
    }

    partial void OnStopChanged(EditorOption<string?> value)
    {
        _definition.Stop = value.Value;
        _changed();
    }

    partial void OnTestReportChanged(string value) => WriteTestReport(r => r.Path = GeneralTabViewModel.NullIfEmpty(value));

    partial void OnRerunParamChanged(EditorOption<string?> value) => WriteTestReport(r => r.RerunParam = value.Value);

    partial void OnRerunScopeChanged(EditorOption<RerunBy> value) =>
        WriteTestReport(r => r.RerunBy = value.Value == RerunBy.Suite ? RerunBy.Suite : null);

    private void WriteTestReport(Action<TestReportDefinition> change)
    {
        var report = _definition.TestReport ?? new TestReportDefinition();
        change(report);
        _definition.TestReport = report.Path is null && report.IsPlain ? null : report;
        _changed();
    }

    [RelayCommand]
    private void AddArtifact()
    {
        Artifacts.Add(new ArtifactRowViewModel(new ArtifactDefinition(), WriteArtifacts));
        WriteArtifacts();
    }

    [RelayCommand]
    private void RemoveArtifact(ArtifactRowViewModel? row)
    {
        if (row is not null && Artifacts.Remove(row))
            WriteArtifacts();
    }

    private void WriteReady()
    {
        var pattern = GeneralTabViewModel.NullIfEmpty(ReadyPattern);
        var open = GeneralTabViewModel.NullIfEmpty(OpenUrl);
        if (pattern is null && open is null)
            _definition.Ready = null;
        else
        {
            _definition.Ready ??= new ReadyDefinition();
            _definition.Ready.Pattern = pattern;
            _definition.Ready.Open = open;
        }
        _changed();
        TestPattern();
    }

    private void WriteArtifacts()
    {
        _definition.Artifacts = Artifacts.Count == 0 ? null : Artifacts.Select(a => a.Artifact).ToList();
        _changed();
    }
}
