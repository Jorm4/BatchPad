using BatchPad.Core.Running;

namespace BatchPad.App.Services;

/// <summary>An <see cref="AdoptedRun"/> as a run tab sees it: one note instead of output.</summary>
public sealed class AdoptedProcess(AdoptedRun run) : IRunProcess
{
    public Task<RunResult> Completion => run.Completion;
    public string? WaitingForLock => null;
    public int? ProcessId => run.ProcessId;

    public event Action? WaitingChanged
    {
        add { }
        remove { }
    }

    public IDisposable Subscribe(Action<OutputLine> onLine)
    {
        onLine(new OutputLine($"Started in an earlier session (process {run.ProcessId}); its output is not shown.", OutputStream.Info));
        return Disposable.None;
    }

    public Task StopAsync(Func<bool>? stopCompanion = null) =>
        run.StopAsync(stopCompanion is null ? TimeSpan.Zero : StopCoordinator.CompanionGrace, stopCompanion);

    public void Dispose() => run.Dispose();
}
