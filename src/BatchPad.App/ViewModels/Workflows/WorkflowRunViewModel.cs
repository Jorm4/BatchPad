using System.Collections.ObjectModel;
using BatchPad.App.Services;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Workflows;

/// <summary>Stops a step that outlives its workflow and opens its ready URL, artifacts and source locations.</summary>
public sealed record StepActions(Func<RunHandle, RunRequest, Task> Stop, IShellOpener Opener, SourceOpener? Sources = null);

/// <summary>
/// A row of a workflow run tab's step list, with its own log: a step, a <c>forEach</c> item, a parallel group or its member,
/// or a step of a nested workflow, indented by <see cref="Depth"/>.
/// </summary>
public sealed partial class StepRowViewModel(StepRun step, string name, IUiDispatcher dispatcher, StepActions? actions = null, int depth = 0,
    Action? onErrorSelected = null)
    : ObservableObject, IDisposable
{
    private IDisposable? _subscription;
    private RunHandle? _subscribed;

    public StepRun Step { get; } = step;
    public string Name { get; } = step.Item ?? (step.IsGroup ? "Parallel" : name);
    public int Depth { get; } = depth;
    public double IndentWidth => Depth * 16;
    public bool IsItem => Step.Item is not null;
    public string AutomationId => IsItem ? $"Step_{Step.Id}_{Step.Item}" : $"Step_{Step.Id}";
    public OutputLog Log { get; } = new(onErrorSelected);
    public ObservableCollection<OutputLineViewModel> Lines => Log.Lines;

    /// <summary>Whether the rows of <see cref="StepRun.Nested"/> have been added under this one.</summary>
    internal bool ShowsNested { get; set; }

    public string? OutputsText => Step.Outputs.Count == 0 ? null : string.Join("  ", Step.Outputs.Select(o => $"{o.Key}={o.Value}"));

    public string? AttemptsText => Step.MaxAttempts > 1 && Step.Attempts.Count > 0
        ? $"attempt {Math.Min(Step.Attempts.Count + (Step.Status == StepStatus.Running ? 1 : 0), Step.MaxAttempts)} of {Step.MaxAttempts}"
        : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(StatusText), nameof(TimeText))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(OpenReadyUrlCommand))]
    private StepStatus status = step.Status;

    public string? ReadyUrl => Step.ReadySignal?.Url;
    public ObservableCollection<ArtifactLinkViewModel> Artifacts { get; } = [];

    private bool CanStop() => actions is not null && Status == StepStatus.Ready && Step is { Handle.Completion.IsCompleted: false, Request: not null };

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        await actions!.Stop(Step.Handle!, Step.Request!);
        Status = StepStatus.Stopped;
    }

    private bool CanOpenReadyUrl() => actions is not null && ReadyUrl is not null && Status == StepStatus.Ready;

    [RelayCommand(CanExecute = nameof(CanOpenReadyUrl))]
    private void OpenReadyUrl() => actions!.Opener.Open(ReadyUrl!);

    public string Glyph => Status switch
    {
        StepStatus.Succeeded or StepStatus.Ready or StepStatus.Reused => "✓",
        StepStatus.Running => "●",
        StepStatus.Failed => "✗",
        StepStatus.Skipped or StepStatus.Stopped => "–",
        _ => "○",
    };

    public string StatusText => Status switch
    {
        StepStatus.Pending => "waiting",
        StepStatus.Running => "running",
        StepStatus.Ready => "ready",
        StepStatus.Skipped => "skipped",
        StepStatus.Stopped => "stopped",
        StepStatus.Reused => "earlier run",
        _ when Step.Result is { } result => RunViewModel.StatusOf(result),
        _ => Step.Items.Count > 0 || Step.IsGroup || Step.Nested is not null ? Status == StepStatus.Succeeded ? "passed" : "failed" : "failed to start",
    };

    public string TimeText => Step.Duration is { } duration ? OutputTabViewModel.FormatDuration(duration) : "";

    public void Refresh()
    {
        if (Step.Handle is { } handle && handle != _subscribed)
        {
            _subscription?.Dispose();
            _subscribed = handle;
            if (Step.Attempts.Count > 0)
                Log.Add(new OutputLineViewModel($"Attempt {Step.Attempts.Count + 1} of {Step.MaxAttempts}", OutputStream.Info));
            _subscription = Subscribe(handle, Step.Request);
        }
        if (Step.Error is { } error && !Lines.Any(l => l.Text == error))
            Log.Add(new OutputLineViewModel(error, OutputStream.Stderr));
        if (Artifacts.Count == 0 && actions is not null)
            foreach (var artifact in Step.Artifacts)
                Artifacts.Add(new ArtifactLinkViewModel(artifact, actions.Opener));
        foreach (var link in Artifacts)
            link.Exists = link.Artifact.Exists;
        if (Status != StepStatus.Stopped)
            Status = Step.Status;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ReadyUrl));
        StopCommand.NotifyCanExecuteChanged();
        OpenReadyUrlCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(OutputsText));
        OnPropertyChanged(nameof(AttemptsText));
    }

    private IDisposable Subscribe(RunHandle handle, RunRequest? request)
    {
        var parser = new OutputLineParser(request?.Script.ErrorPatterns);
        var secrets = request is null ? [] : SecretMasker.SecretValues(request);
        var links = request is not null && actions?.Sources is { } sources
            ? new SourceLinks(() => RunPlanner.WorkingDirectoryFor(request), sources)
            : null;
        var poster = new OutputPoster(Log, dispatcher);
        return handle.Subscribe(line =>
        {
            if (line.Stream != OutputStream.Stdout || !StepOutputs.IsSetLine(line.Text))
                poster.Add(OutputLineViewModel.From(parser.Parse(SecretMasker.Mask(line.Text, secrets)), line.Stream, links));
        });
    }

    public void Dispose() => _subscription?.Dispose();
}

/// <summary>A workflow run as one output tab (§4.1): a step list on the left, the selected step's log on the right.</summary>
public sealed partial class WorkflowRunViewModel : OutputTabViewModel
{
    private readonly WorkflowRun _run;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<StepRun, string> _nameOf;
    private readonly StepActions? _actions;
    private readonly Action<WorkflowRequest>? _rerun;
    private readonly Dictionary<StepRun, StepRowViewModel> _rows = [];

    public WorkflowRunViewModel(string title, NodeViewModel? node, WorkflowRun run, Func<StepRun, string> nameOf, IUiDispatcher dispatcher,
        StepActions? actions = null, Action<WorkflowRequest>? rerun = null)
        : base(title, node)
    {
        _run = run;
        _rerun = rerun;
        _dispatcher = dispatcher;
        _nameOf = nameOf;
        _actions = actions;
        node?.OnRunStarted();
        foreach (var step in run.Steps)
            AddRows(step, 0, Steps.Count);
        selectedStep = Steps.FirstOrDefault();
        run.StepChanged += OnStepChanged;
        run.WaitingChanged += OnWaitingChanged;
        OnWaitingChanged();
        foreach (var step in run.Steps.SelectMany(s => s.SelfAndChildren()))
            dispatcher.Post(() => Update(step));
        Finished = WatchAsync();
    }

    public ObservableCollection<StepRowViewModel> Steps { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Log))]
    private StepRowViewModel? selectedStep;

    public override OutputLog? Log => SelectedStep?.Log;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(Succeeded), nameof(StatusText), nameof(DurationText), nameof(CanRerunFromFailed),
        nameof(RerunLabel))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand), nameof(RerunFromFailedCommand))]
    private WorkflowResult? result;

    public bool CanRerunFromFailed => _rerun is not null && Result?.Outcome == WorkflowOutcome.Failed && _run.FailedStep is not null;

    public string RerunLabel => _run.FailedStep is { } failed ? $"Re-run from {_nameOf(failed)}" : "";

    [RelayCommand(CanExecute = nameof(CanRerunFromFailed))]
    private void RerunFromFailed() => _rerun!(WorkflowRequest.ResumeFrom(_run, _run.FailedStep!.Id));

    public override bool IsRunning => Result is null;
    public override bool Succeeded => Result?.Succeeded == true;

    public int FinishedSteps => _run.Steps.Count(s => s.Status is not (StepStatus.Pending or StepStatus.Running));
    public int StepCount => _run.Steps.Count;

    public override string StatusText => Result?.Outcome switch
    {
        null => _run.WaitingForLock is { } name ? $"waiting for lock {name}" : "running",
        WorkflowOutcome.Succeeded => Steps.Any(s => s.Status == StepStatus.Ready) ? "running · ready" : "passed",
        WorkflowOutcome.Stopped => "stopped",
        _ => "failed",
    };

    public override string DurationText => Result is { } finished ? FormatDuration(finished.Duration) : "";

    protected override Task StopRunAsync() => _run.StopAsync();

    private void OnWaitingChanged() => _dispatcher.Post(() =>
    {
        if (!IsRunning)
            return;
        OnPropertyChanged(nameof(StatusText));
        Node?.OnRunWaiting(_run.WaitingForLock);
    });

    public override void Dispose()
    {
        _run.StepChanged -= OnStepChanged;
        _run.WaitingChanged -= OnWaitingChanged;
        foreach (var step in Steps)
            step.Dispose();
    }

    private void OnStepChanged(StepRun step) => _dispatcher.Post(() => Update(step));

    private void Update(StepRun step)
    {
        if (!_rows.TryGetValue(step, out var row))
        {
            if (_rows.Values.FirstOrDefault(r => r.Step.Items.Contains(step)) is not { } parent)
                return;
            row = NewRow(step, parent.Depth + 1);
            Steps.Insert(EndOf(parent), row);
        }
        if (step.Nested is { } nested && !row.ShowsNested)
        {
            row.ShowsNested = true;
            var start = EndOf(row);
            var at = start;
            foreach (var inner in nested.Steps)
                at = AddRows(inner, row.Depth + 1, at);
            for (var i = start; i < at; i++)
                Steps[i].Refresh();
        }
        row.Refresh();
        OnPropertyChanged(nameof(FinishedSteps));
        if (step.Status == StepStatus.Running && (SelectedStep is null || SelectedStep.Status != StepStatus.Running))
            SelectedStep = row;
    }

    private static IEnumerable<StepRun> WithNested(IEnumerable<StepRun> steps) =>
        steps.SelectMany(s => s.SelfAndChildren()).SelectMany(s => s.Nested is { } nested ? [s, .. WithNested(nested.Steps)] : new[] { s });

    private StepRowViewModel NewRow(StepRun step, int depth)
    {
        var row = new StepRowViewModel(step, _nameOf(step), _dispatcher, _actions, depth, () => AutoScroll = false);
        _rows[step] = row;
        return row;
    }

    /// <summary>Adds rows for <paramref name="step"/> and its group members at <paramref name="at"/>; returns the index after them.</summary>
    private int AddRows(StepRun step, int depth, int at)
    {
        Steps.Insert(at++, NewRow(step, depth));
        foreach (var member in step.Members)
            at = AddRows(member, depth + 1, at);
        return at;
    }

    /// <summary>The index just past <paramref name="row"/> and the rows indented under it.</summary>
    private int EndOf(StepRowViewModel row)
    {
        var index = Steps.IndexOf(row) + 1;
        while (index < Steps.Count && Steps[index].Depth > row.Depth)
            index++;
        return index;
    }

    private async Task WatchAsync()
    {
        var outcome = await _run.Completion.ConfigureAwait(false);
        var shown = new TaskCompletionSource();
        _dispatcher.Post(() =>
        {
            foreach (var step in WithNested(_run.Steps))
                Update(step);
            Result = outcome;
            Node?.OnRunFinished(new RunResult(
                outcome.Outcome switch
                {
                    WorkflowOutcome.Stopped => RunOutcome.Stopped,
                    _ => RunOutcome.Exited,
                },
                outcome.Succeeded ? 0 : 1, outcome.Duration));
            shown.SetResult();
        });
        await shown.Task.ConfigureAwait(false);
    }
}
