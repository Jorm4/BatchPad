using System.Collections.ObjectModel;
using BatchPad.App.Services;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Workflows;

/// <summary>Stops a step that outlives its workflow and opens its ready URL and artifacts.</summary>
public sealed record StepActions(Func<RunHandle, RunRequest, Task> Stop, IShellOpener Opener);

/// <summary>A step, or one <c>forEach</c> item of it, in a workflow run tab's step list, with its own log.</summary>
public sealed partial class StepRowViewModel(StepRun step, string name, IUiDispatcher dispatcher, StepActions? actions = null)
    : ObservableObject, IDisposable
{
    private IDisposable? _subscription;

    public StepRun Step { get; } = step;
    public string Name { get; } = step.Item ?? name;
    public bool IsItem => Step.Item is not null;
    public string AutomationId => IsItem ? $"Step_{Step.Id}_{Step.Item}" : $"Step_{Step.Id}";
    public ObservableCollection<OutputLineViewModel> Lines { get; } = [];

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
        StepStatus.Succeeded or StepStatus.Ready => "✓",
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
        _ when Step.Result is { } result => RunViewModel.StatusOf(result),
        _ => Step.Items.Count > 0 ? Status == StepStatus.Succeeded ? "passed" : "failed" : "failed to start",
    };

    public string TimeText => Step.Duration is { } duration ? OutputTabViewModel.FormatDuration(duration) : "";

    public void Refresh()
    {
        if (_subscription is null && Step.Handle is { } handle)
            _subscription = handle.Subscribe(line => dispatcher.Post(() => Lines.Add(new OutputLineViewModel(line.Text, line.Stream))));
        if (Step.Error is { } error && !Lines.Any(l => l.Text == error))
            Lines.Add(new OutputLineViewModel(error, OutputStream.Stderr));
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

    public WorkflowRunViewModel(string title, NodeViewModel? node, WorkflowRun run, Func<StepRun, string> nameOf, IUiDispatcher dispatcher,
        StepActions? actions = null)
        : base(title, node)
    {
        _run = run;
        _dispatcher = dispatcher;
        _nameOf = nameOf;
        _actions = actions;
        node?.OnRunStarted();
        foreach (var step in run.Steps)
            Steps.Add(new StepRowViewModel(step, nameOf(step), dispatcher, actions));
        selectedStep = Steps.FirstOrDefault();
        run.StepChanged += OnStepChanged;
        foreach (var step in run.Steps.SelectMany(s => s.Items.Prepend(s)))
            dispatcher.Post(() => Update(step));
        Finished = WatchAsync();
    }

    public ObservableCollection<StepRowViewModel> Steps { get; } = [];

    [ObservableProperty]
    private StepRowViewModel? selectedStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(Succeeded), nameof(StatusText), nameof(DurationText))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private WorkflowResult? result;

    public override bool IsRunning => Result is null;
    public override bool Succeeded => Result?.Succeeded == true;

    public override string StatusText => Result?.Outcome switch
    {
        null => "running",
        WorkflowOutcome.Succeeded => Steps.Any(s => s.Status == StepStatus.Ready) ? "running · ready" : "passed",
        WorkflowOutcome.Stopped => "stopped",
        _ => "failed",
    };

    public override string DurationText => Result is { } finished ? FormatDuration(finished.Duration) : "";

    protected override Task StopRunAsync() => _run.StopAsync();

    public override void Dispose()
    {
        _run.StepChanged -= OnStepChanged;
        foreach (var step in Steps)
            step.Dispose();
    }

    private void OnStepChanged(StepRun step) => _dispatcher.Post(() => Update(step));

    private void Update(StepRun step)
    {
        var row = Steps.FirstOrDefault(r => r.Step == step);
        if (row is null)
        {
            if (step.Item is null || Steps.LastOrDefault(r => r.Step.Id == step.Id && r.Step.Step == step.Step) is not { } after)
                return;
            row = new StepRowViewModel(step, _nameOf(step), _dispatcher, _actions);
            Steps.Insert(Steps.IndexOf(after) + 1, row);
        }
        row.Refresh();
        if (step.Status == StepStatus.Running && (SelectedStep is null || SelectedStep.Status != StepStatus.Running))
            SelectedStep = row;
    }

    private async Task WatchAsync()
    {
        var outcome = await _run.Completion.ConfigureAwait(false);
        var shown = new TaskCompletionSource();
        _dispatcher.Post(() =>
        {
            foreach (var step in _run.Steps.SelectMany(s => s.Items.Prepend(s)))
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
