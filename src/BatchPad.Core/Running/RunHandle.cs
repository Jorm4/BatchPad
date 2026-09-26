namespace BatchPad.Core.Running;

public enum OutputStream { Stdout, Stderr, Info }

public sealed record OutputLine(string Text, OutputStream Stream);

public enum RunOutcome { Exited, Stopped, TimedOut, FailedToStart }

public sealed record RunResult(RunOutcome Outcome, int ExitCode, TimeSpan Duration)
{
    public bool Succeeded => Outcome == RunOutcome.Exited && ExitCode == 0;
}

/// <summary>
/// A run in progress. Subscribers get the kept lines from the start, then live lines in order on a reader thread,
/// so they must not block; output can outlive <see cref="Completion"/> while a detached child holds it open.
/// </summary>
public sealed class RunHandle : IObservable<OutputLine>, IRunOutput, IDisposable
{
    public static readonly TimeSpan DefaultStopGrace = TimeSpan.FromSeconds(3);
    public const int KeptLines = 20_000;
    private static readonly TimeSpan OutputDrainLimit = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();

    // Held while delivering, so observers see lines in order without holding _lock; always taken before _lock.
    private readonly Lock _delivery = new();
    private readonly Queue<OutputLine> _lines = new();
    private IObserver<OutputLine>[] _observers = [];
    private int _droppedLines;
    private readonly List<StartedProcess> _processes = [];
    private readonly TaskCompletionSource<RunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopRequested = new();
    private LockLease? _locks;
    private readonly long _createdTimestamp;
    private long _startTimestamp;
    private RunOutcome? _stopOutcome;
    private bool _outputEnded;

    internal RunHandle(IReadOnlyList<RunSpec> specs, TimeProvider time)
    {
        Specs = specs;
        _time = time;
        _createdTimestamp = time.GetTimestamp();
    }

    public IReadOnlyList<RunSpec> Specs { get; }
    public Task<RunResult> Completion => _completion.Task;
    public DateTimeOffset StartedAt { get; private set; }

    RunHandle IRunOutput.Handle => this;

    /// <summary>How long the run waited for its locks before it started.</summary>
    public TimeSpan Queued { get; private set; }

    /// <summary>The lock this run is queued behind; null once it has started.</summary>
    public string? WaitingForLock { get; private set; }

    /// <summary>Raised on a worker thread when <see cref="WaitingForLock"/> changes.</summary>
    public event Action? WaitingChanged;

    internal CancellationToken StopRequested => _stopRequested.Token;

    /// <summary>The process currently running (the latest one once finished).</summary>
    public int ProcessId
    {
        get
        {
            lock (_lock)
                return _processes[^1].Id;
        }
    }

    /// <summary>The last <see cref="KeptLines"/> lines, after a note of how many earlier ones were dropped.</summary>
    public IReadOnlyList<OutputLine> Output
    {
        get
        {
            lock (_lock)
                return KeptOutput();
        }
    }

    public IDisposable Subscribe(IObserver<OutputLine> observer)
    {
        lock (_delivery)
        {
            List<OutputLine> replay;
            bool ended;
            lock (_lock)
            {
                replay = KeptOutput();
                ended = _outputEnded;
                if (!ended)
                    _observers = [.. _observers, observer];
            }
            foreach (var line in replay)
                Deliver(observer, line);
            if (ended)
            {
                Complete(observer);
                return Disposable.None;
            }
        }
        return Disposable.From(() =>
        {
            lock (_lock)
                _observers = [.. _observers.Where(o => o != observer)];
        });
    }

    public IDisposable Subscribe(Action<OutputLine> onLine) => Subscribe(new ActionObserver(onLine));

    public Task StopAsync() => StopAsync(RunOutcome.Stopped, DefaultStopGrace);

    /// <summary>
    /// Asks politely — <paramref name="stopCompanion"/> if given and it returns true, else WM_CLOSE to GUI windows —
    /// then after <paramref name="grace"/> terminates every job, detached children included.
    /// </summary>
    public async Task StopAsync(RunOutcome outcome, TimeSpan grace, Func<bool>? stopCompanion = null)
    {
        StartedProcess? current = null;
        lock (_lock)
        {
            _stopOutcome ??= outcome;
            if (_processes.Count == 0)
                CancelWaiting(_stopOutcome.Value);
            else
                current = _processes[^1];
        }
        if (current is null)
        {
            await Completion;
            return;
        }
        if (!Completion.IsCompleted && grace > TimeSpan.Zero && AskToStop(current, stopCompanion))
            await Task.WhenAny(current.Exited, Task.Delay(grace, _time));

        StartedProcess[] all;
        lock (_lock)
            all = [.. _processes];
        foreach (var process in all)
            process.Job.Terminate(1);
        await Completion;
    }

    private static bool AskToStop(StartedProcess current, Func<bool>? stopCompanion) =>
        stopCompanion?.Invoke() == true || current.Job.CloseWindows() > 0;

    /// <summary>Releases this run's locks before it ends, as a workflow's long-running last step does at ready.</summary>
    public void ReleaseLocks() => Interlocked.Exchange(ref _locks, null)?.Dispose();

    public void Dispose()
    {
        lock (_lock)
        {
            if (_processes.Count == 0)
            {
                _stopOutcome ??= RunOutcome.Stopped;
                CancelWaiting(_stopOutcome.Value);
            }
            foreach (var process in _processes)
                process.Dispose();
        }
        EndOutput();
    }

    internal void Wait(string lockName)
    {
        WaitingForLock = lockName;
        Publish($"Waiting for lock '{lockName}'…", OutputStream.Info);
        WaitingChanged?.Invoke();
    }

    /// <exception cref="RunException">The first process could not be started.</exception>
    internal void Begin(LockLease? locks = null)
    {
        var wasWaiting = WaitingForLock is not null;
        lock (_lock)
        {
            if (_stopOutcome is not null)
            {
                locks?.Dispose();
                return;
            }
            _locks = locks;
            WaitingForLock = null;
            StartedAt = _time.GetLocalNow();
            _startTimestamp = _time.GetTimestamp();
            Queued = _time.GetElapsedTime(_createdTimestamp, _startTimestamp);
            try
            {
                _processes.Add(ProcessRunner.Launch(Specs[0], Publish));
            }
            catch
            {
                ReleaseLocks();
                throw;
            }
        }
        if (wasWaiting)
            WaitingChanged?.Invoke();
        _ = RunAsync();
    }

    internal void FailToStart(string message)
    {
        Publish(message, OutputStream.Info);
        Finish(new RunResult(RunOutcome.FailedToStart, -1, TimeSpan.Zero));
    }

    private void CancelWaiting(RunOutcome outcome)
    {
        _stopRequested.Cancel();
        _ = Task.Run(() => Finish(new RunResult(outcome, -1, TimeSpan.Zero)));
    }

    private void Finish(RunResult result)
    {
        ReleaseLocks();
        if (_completion.TrySetResult(result))
            EndOutput();
    }

    private async Task RunAsync()
    {
        RunResult result;
        string? launchError = null;
        for (var index = 0; ; index++)
        {
            StartedProcess current;
            lock (_lock)
                current = _processes[^1];

            using var timeout = Specs[index].Timeout is { } limit ? new CancellationTokenSource(limit, _time) : null;
            using var timeoutRegistration = timeout?.Token.Register(() =>
            {
                Publish($"Timed out after {Specs[index].Timeout}; stopping.", OutputStream.Info);
                _ = StopAsync(RunOutcome.TimedOut, TimeSpan.Zero);
            });

            var exitCode = await current.Exited;
            await Task.WhenAny(current.Output, Task.Delay(OutputDrainLimit));

            lock (_lock)
            {
                if (_stopOutcome is { } stopped)
                {
                    result = new RunResult(stopped, exitCode, Elapsed);
                    break;
                }
                if (exitCode != 0 || index == Specs.Count - 1)
                {
                    result = new RunResult(RunOutcome.Exited, exitCode, Elapsed);
                    break;
                }
                try
                {
                    _processes.Add(ProcessRunner.Launch(Specs[index + 1], Publish));
                }
                catch (RunException ex)
                {
                    launchError = ex.Message;
                    result = new RunResult(RunOutcome.FailedToStart, -1, Elapsed);
                    break;
                }
            }
        }
        if (launchError is not null)
            Publish(launchError, OutputStream.Info);
        ReleaseLocks();
        _completion.SetResult(result);

        Task[] outputs;
        lock (_lock)
            outputs = [.. _processes.Select(p => p.Output)];
        await Task.WhenAll(outputs);
        EndOutput();
    }

    private TimeSpan Elapsed => _time.GetElapsedTime(_startTimestamp);

    private void Publish(string text, OutputStream stream)
    {
        var line = new OutputLine(text, stream);
        lock (_delivery)
        {
            IObserver<OutputLine>[] observers;
            lock (_lock)
            {
                _lines.Enqueue(line);
                if (_lines.Count > KeptLines)
                {
                    _lines.Dequeue();
                    _droppedLines++;
                }
                observers = _observers;
            }
            foreach (var observer in observers)
                Deliver(observer, line);
        }
    }

    private List<OutputLine> KeptOutput() => _droppedLines == 0
        ? [.. _lines]
        : [new OutputLine($"… {_droppedLines} earlier lines not kept", OutputStream.Info), .. _lines];

    private void EndOutput()
    {
        lock (_delivery)
        {
            IObserver<OutputLine>[] observers;
            lock (_lock)
            {
                if (_outputEnded)
                    return;
                _outputEnded = true;
                observers = _observers;
                _observers = [];
            }
            foreach (var observer in observers)
                Complete(observer);
        }
    }

    // A failing subscriber must not stop the pipe reader or keep the others from their lines.
    private static void Deliver(IObserver<OutputLine> observer, OutputLine line)
    {
        try
        {
            observer.OnNext(line);
        }
        catch (Exception)
        {
        }
    }

    private static void Complete(IObserver<OutputLine> observer)
    {
        try
        {
            observer.OnCompleted();
        }
        catch (Exception)
        {
        }
    }

    private sealed class ActionObserver(Action<OutputLine> onLine) : IObserver<OutputLine>
    {
        public void OnNext(OutputLine value) => onLine(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
