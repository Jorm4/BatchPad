using BatchPad.Core.Running;
using BatchPad.Core.Trust;

namespace BatchPad.Core.Workflows;

/// <summary>Starts and stops a workflow's script steps; tests substitute their own.</summary>
public interface IStepLauncher
{
    /// <exception cref="RunException">The step could not be started.</exception>
    RunHandle Start(RunRequest request);

    Task StopAsync(RunHandle run, RunRequest request);
}

public sealed class GatedStepLauncher(RunGate gate, InterpreterLocator interpreters) : IStepLauncher
{
    private readonly StopCoordinator _stopper = new(gate, interpreters);

    public RunHandle Start(RunRequest request) => gate.Start(request, interpreters);

    public async Task StopAsync(RunHandle run, RunRequest request)
    {
        using var companion = await _stopper.StopAsync(run, request);
    }
}
