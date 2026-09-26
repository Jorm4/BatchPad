using System.Diagnostics;

namespace BatchPad.Core.Running;

/// <summary>
/// A long-running process started by an earlier session. Its output went with that session, and its job object too,
/// so Stop ends it by its stop companion or by terminating its process tree.
/// </summary>
public sealed class AdoptedRun : IDisposable
{
    private readonly Process _process;
    private readonly RunningRegistry _registry;
    private readonly TaskCompletionSource<RunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private volatile bool _stopping;

    private AdoptedRun(RunningEntry entry, Process process, RunningRegistry registry)
    {
        Entry = entry;
        _process = process;
        _registry = registry;
        _ = WatchAsync();
    }

    internal static AdoptedRun? TryOpen(RunningEntry entry, RunningRegistry registry)
    {
        try
        {
            return new AdoptedRun(entry, Process.GetProcessById(entry.ProcessId), registry);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public RunningEntry Entry { get; }
    public int ProcessId => Entry.ProcessId;
    public Task<RunResult> Completion => _completion.Task;

    /// <param name="stopCompanion">Starts the script's stop companion; true when it started, so the process gets <paramref name="grace"/> to exit.</param>
    public async Task StopAsync(TimeSpan grace, Func<bool>? stopCompanion = null)
    {
        if (Completion.IsCompleted)
            return;
        _stopping = true;
        if (stopCompanion?.Invoke() == true && await Task.WhenAny(Completion, Task.Delay(grace)) == Completion)
            return;
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        await Completion;
    }

    public void Dispose() => _process.Dispose();

    private async Task WatchAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
        try
        {
            _registry.Remove(Entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            _completion.TrySetResult(new RunResult(_stopping ? RunOutcome.Stopped : RunOutcome.Exited, ExitCodeOrUnknown(), _clock.Elapsed));
        }
    }

    private int ExitCodeOrUnknown()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }
}
