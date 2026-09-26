using System.Diagnostics;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.Core.Workflows;

public enum StepStatus { Pending, Running, Ready, Succeeded, Failed, Skipped, Stopped, Reused }

public enum WorkflowOutcome { Succeeded, Failed, Stopped }

/// <summary>One finished try of a step that has <c>retry</c>.</summary>
public sealed record StepAttempt(RunHandle Handle, RunResult Result);

public sealed record WorkflowResult(WorkflowOutcome Outcome, TimeSpan Duration)
{
    public bool Succeeded => Outcome == WorkflowOutcome.Succeeded;
}

/// <summary>A step of a running workflow, one <c>forEach</c> item row of such a step, or a parallel group.</summary>
public sealed class StepRun
{
    internal StepRun(WorkflowStep step, string id, string? item = null)
    {
        Step = step;
        Id = id;
        Item = item;
        Members = step.Parallel is { } members && item is null
            ? [.. members.Select((member, index) => new StepRun(member, member.Id ?? $"{id}-{index + 1}"))]
            : [];
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

    public IReadOnlyList<StepRun> Members { get; }

    public bool IsGroup => Step.IsGroup;

    public RunResult? Result { get; internal set; }
    public ReadySignal? ReadySignal { get; internal set; }
    public IReadOnlyList<ResolvedArtifact> Artifacts { get; internal set; } = [];

    public IReadOnlyDictionary<string, string> Outputs { get; internal set; } = new Dictionary<string, string>();

    public IReadOnlyList<StepAttempt> Attempts { get; internal set; } = [];

    public int MaxAttempts => 1 + Math.Max(0, Step.Retry?.Count ?? 0);

    /// <summary>Why the step failed without a result: a missing target, a template or start error.</summary>
    public string? Error { get; internal set; }

    public TimeSpan? Duration => Result?.Duration;

    internal IReadOnlyList<StepRun> SetItems(IEnumerable<string> items)
    {
        Items = [.. items.Select(item => new StepRun(Step, Id, item))];
        return Items;
    }

    public IEnumerable<StepRun> SelfAndChildren() => [this, .. Items, .. Members.SelectMany(m => m.SelfAndChildren())];
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
    private readonly CancellationTokenSource _stopRequested = new();
    private volatile bool _stopping;

    internal WorkflowRun(WorkflowRequest request, IReadOnlyList<StepRun> steps, IStepLauncher launcher, object? lockOwner)
    {
        Request = request;
        Workflow = request.Workflow;
        Steps = steps;
        _launcher = launcher;
        LockOwner = lockOwner ?? this;
    }

    /// <summary>The workflow's <c>lock</c> while it is queued behind another run; null once it runs.</summary>
    public string? WaitingForLock { get; private set; }

    public event Action? WaitingChanged;

    internal object LockOwner { get; }
    internal CancellationToken StopRequested => _stopRequested.Token;

    public WorkflowRequest Request { get; }
    public WorkflowNode Workflow { get; }

    /// <summary>The id of the workflow's history record, known up front so its steps' records can point at it.</summary>
    public string RunId { get; } = RunRecord.NewId(DateTimeOffset.UtcNow);

    /// <summary>The first step whose failure failed the workflow; a re-run starts there.</summary>
    public StepRun? FailedStep { get; internal set; }

    /// <summary>Each finished step's <c>result</c>, <c>exitCode</c> and outputs, as <c>${steps.*}</c> reads them.</summary>
    public Dictionary<string, IReadOnlyDictionary<string, string>> StepResults { get; } = [];
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
        await _stopRequested.CancelAsync();
        await Task.WhenAll(Steps.SelectMany(s => s.SelfAndChildren()).Select(StopStepAsync));
        await Completion;
    }

    internal Task StopStepAsync(StepRun step) =>
        step.Nested is { } nested ? nested.StopAsync()
        : step is { Handle: { } handle, Request: { } request } && !handle.Completion.IsCompleted ? _launcher.StopAsync(handle, request)
        : Task.CompletedTask;

    internal void Wait(string? lockName)
    {
        if (WaitingForLock == lockName)
            return;
        WaitingForLock = lockName;
        WaitingChanged?.Invoke();
    }

    internal void Update(StepRun step, StepStatus status)
    {
        step.Status = status;
        Raise(step);
    }

    internal void Raise(StepRun step) => StepChanged?.Invoke(step);

    internal void Complete(WorkflowOutcome outcome) => _completion.TrySetResult(new WorkflowResult(outcome, _clock.Elapsed));
}
