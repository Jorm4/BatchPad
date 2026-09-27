using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Services;

/// <summary>A started run as the UI sees it; <see cref="RunHandle"/> in the app, a fake in tests.</summary>
public interface IRunProcess : IRunOutput, IDisposable
{
    /// <param name="stopCompanion">Starts the script's stop companion; true when it started, so the run gets longer to exit.</param>
    Task StopAsync(Func<bool>? stopCompanion = null);

    string? WaitingForLock { get; }

    /// <summary>Raised on a worker thread.</summary>
    event Action? WaitingChanged;

    /// <summary>The process running now; null while queued for a lock, or when there is none to name.</summary>
    int? ProcessId => null;
}

public interface IRunLauncher
{
    /// <exception cref="UntrustedWorkspaceException" />
    /// <exception cref="RunException" />
    /// <exception cref="LockBusyException">A lock is held and <paramref name="waitForLocks"/> is false.</exception>
    IRunProcess Start(RunRequest request, bool waitForLocks = true);
}

public sealed class GatedRunLauncher(RunGate gate, InterpreterLocator interpreters) : IRunLauncher
{
    public IRunProcess Start(RunRequest request, bool waitForLocks = true) =>
        new HandleProcess(gate.Start(request, interpreters, waitForLocks));

    private sealed class HandleProcess(RunHandle handle) : IRunProcess
    {
        public IDisposable Subscribe(Action<OutputLine> onLine) => handle.Subscribe(onLine);
        public Task<RunResult> Completion => handle.Completion;
        public RunHandle Handle => handle;
        public Task StopAsync(Func<bool>? stopCompanion = null) =>
            handle.StopAsync(RunOutcome.Stopped, stopCompanion is null ? RunHandle.DefaultStopGrace : StopCoordinator.CompanionGrace, stopCompanion);
        public string? WaitingForLock => handle.WaitingForLock;

        public int? ProcessId
        {
            get
            {
                try
                {
                    return handle.WaitingForLock is null ? handle.ProcessId : null;
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }
        }

        public event Action? WaitingChanged
        {
            add => handle.WaitingChanged += value;
            remove => handle.WaitingChanged -= value;
        }

        public void Dispose() => handle.Dispose();
    }
}

public interface IWorkflowLauncher
{
    /// <exception cref="WorkflowException">The workflow runs itself.</exception>
    WorkflowRun Start(LoadedWorkspace workspace, WorkflowRequest request);

    /// <summary>Stops a step still running after its workflow finished, such as a server that became ready.</summary>
    Task StopStepAsync(RunHandle run, RunRequest request);
}

public sealed class GatedWorkflowLauncher(RunGate gate, InterpreterLocator interpreters, IShellOpener opener, ISecretStore? secrets = null)
    : IWorkflowLauncher
{
    private readonly StopCoordinator _stopper = new(gate, interpreters);

    public WorkflowRun Start(LoadedWorkspace workspace, WorkflowRequest request) =>
        new WorkflowRunner(workspace, gate, interpreters, opener, secrets).Start(request);

    public async Task StopStepAsync(RunHandle run, RunRequest request)
    {
        using var companion = await _stopper.StopAsync(run, request);
        if (companion is not null)
            await companion.Completion;
    }
}
