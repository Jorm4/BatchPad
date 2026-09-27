using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Scheduling;

/// <summary>
/// Starts the scheduler's runs. The scheduler records the run it gets back in history itself, so a host that also
/// shows the run must not record it again (a workflow's steps are the host's to record).
/// </summary>
public interface IScheduleLauncher
{
    /// <exception cref="Exception">The run could not start, e.g. an untrusted workspace or a missing unattended value.</exception>
    IRunOutput Start(RunRequest request);

    /// <param name="trigger">The history trigger for the workflow's step records.</param>
    IRunOutput Start(WorkflowRequest request, string trigger);
}

/// <summary>Runs through the trust gate, with no UI.</summary>
public sealed class GatedScheduleLauncher(LoadedWorkspace workspace, RunGate gate, InterpreterLocator interpreters, ISecretStore? secrets = null)
    : IScheduleLauncher
{
    private readonly WorkflowRunner _workflows = new(workspace, gate, interpreters, secrets: secrets);

    public IRunOutput Start(RunRequest request) => gate.Start(request, interpreters);

    public IRunOutput Start(WorkflowRequest request, string trigger) => new WorkflowRunOutput(_workflows.Start(request));
}

/// <summary>A workflow run seen as one run: exit 0 when it succeeded, with a "step: status" line per step at the end.</summary>
public sealed class WorkflowRunOutput : IRunOutput
{
    private readonly List<Action<OutputLine>> _subscribers = [];

    public WorkflowRunOutput(WorkflowRun run)
    {
        Run = run;
        Completion = run.Completion.ContinueWith(completion =>
        {
            var result = completion.Result;
            Action<OutputLine>[] subscribers;
            lock (_subscribers)
                subscribers = [.. _subscribers];
            foreach (var step in run.Steps)
                foreach (var subscriber in subscribers)
                    subscriber(new OutputLine($"{step.Id}: {step.Status}", OutputStream.Info));
            return new RunResult(result.Outcome == WorkflowOutcome.Stopped ? RunOutcome.Stopped : RunOutcome.Exited, result.Succeeded ? 0 : 1, result.Duration);
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    public WorkflowRun Run { get; }
    public Task<RunResult> Completion { get; }

    public IDisposable Subscribe(Action<OutputLine> onLine)
    {
        lock (_subscribers)
            _subscribers.Add(onLine);
        return new Unsubscriber(() =>
        {
            lock (_subscribers)
                _subscribers.Remove(onLine);
        });
    }

    private sealed class Unsubscriber(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
