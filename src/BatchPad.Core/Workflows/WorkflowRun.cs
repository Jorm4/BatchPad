using System.Diagnostics;
using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.Core.Workflows;

public enum StepStatus { Pending, Running, Ready, Succeeded, Failed, Skipped, Stopped }

public enum WorkflowOutcome { Succeeded, Failed, Stopped }

public sealed record WorkflowResult(WorkflowOutcome Outcome, TimeSpan Duration)
{
    public bool Succeeded => Outcome == WorkflowOutcome.Succeeded;
}

/// <summary>A step of a running workflow, or one <c>forEach</c> item row of such a step.</summary>
public sealed class StepRun
{
    internal StepRun(WorkflowStep step, string id, string? item = null)
    {
        Step = step;
        Id = id;
        Item = item;
    }

    public WorkflowStep Step { get; }
    public string Id { get; }

    /// <summary>The <c>forEach</c> item this row runs; null for a step itself.</summary>
    public string? Item { get; }

    public StepStatus Status { get; internal set; }
    public RunHandle? Handle { get; internal set; }
    public RunRequest? Request { get; internal set; }
    public WorkflowRun? Nested { get; internal set; }
    public IReadOnlyList<StepRun> Items { get; private set; } = [];
    public RunResult? Result { get; internal set; }
    public ReadySignal? ReadySignal { get; internal set; }
    public IReadOnlyList<ResolvedArtifact> Artifacts { get; internal set; } = [];

    /// <summary>Why the step failed without a result: a missing target, a template or start error.</summary>
    public string? Error { get; internal set; }

    public TimeSpan? Duration => Result?.Duration;

    internal StepRun AddItem(string item)
    {
        var row = new StepRun(Step, Id, item);
        Items = [.. Items, row];
        return row;
    }

    internal IEnumerable<StepRun> SelfAndItems() => [this, .. Items];
}

/// <summary>
/// A workflow in progress (§4.1). <see cref="StepChanged"/> fires on a worker thread whenever a step, item row or
/// nested workflow's step changes status.
/// </summary>
public sealed class WorkflowRun
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TaskCompletionSource<WorkflowResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly IStepLauncher _launcher;
    private volatile bool _stopping;

    internal WorkflowRun(WorkflowNode workflow, IReadOnlyList<StepRun> steps, IStepLauncher launcher)
    {
        Workflow = workflow;
        Steps = steps;
        _launcher = launcher;
    }

    public WorkflowNode Workflow { get; }
    public IReadOnlyList<StepRun> Steps { get; }
    public Task<WorkflowResult> Completion => _completion.Task;
    public bool IsStopping => _stopping;

    public event Action<StepRun>? StepChanged;

    /// <summary>Stops every running step, including nested workflows, and skips the rest. A long-running last step that is already ready keeps running.</summary>
    public async Task StopAsync()
    {
        if (Completion.IsCompleted)
            return;
        _stopping = true;
        await Task.WhenAll(Steps.SelectMany(s => s.SelfAndItems()).Select(StopStepAsync));
        await Completion;
    }

    internal Task StopStepAsync(StepRun step) =>
        step.Nested is { } nested ? nested.StopAsync()
        : step is { Handle: { } handle, Request: { } request } && !handle.Completion.IsCompleted ? _launcher.StopAsync(handle, request)
        : Task.CompletedTask;

    internal void Update(StepRun step, StepStatus status)
    {
        step.Status = status;
        Raise(step);
    }

    internal void Raise(StepRun step) => StepChanged?.Invoke(step);

    internal void Complete(WorkflowOutcome outcome) => _completion.TrySetResult(new WorkflowResult(outcome, _clock.Elapsed));
}
