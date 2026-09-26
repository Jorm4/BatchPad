using System.Diagnostics;

namespace BatchPad.Core.Running;

public enum OutputStream { Stdout, Stderr, Info }

public sealed record OutputLine(string Text, OutputStream Stream);

public enum RunOutcome { Exited, Stopped, TimedOut, FailedToStart }

public sealed record RunResult(RunOutcome Outcome, int ExitCode, TimeSpan Duration)
{
    public bool Succeeded => Outcome == RunOutcome.Exited && ExitCode == 0;
}

/// <summary>
/// A run in progress. Subscribers get every line from the start, then live lines on a reader thread,
/// so they must not block; output can outlive <see cref="Completion"/> while a detached child holds it open.
/// </summary>
public sealed class RunHandle : IObservable<OutputLine>, IRunOutput, IDisposable
{
    public static readonly TimeSpan DefaultStopGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OutputDrainLimit = TimeSpan.FromSeconds(1);

    private readonly Lock _lock = new();
    private readonly List<OutputLine> _lines = [];
    private readonly List<IObserver<OutputLine>> _observers = [];
    private readonly List<StartedProcess> _processes = [];
    private readonly TaskCompletionSource<RunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _clock = new();
    private RunOutcome? _stopOutcome;
    private bool _outputEnded;

    internal RunHandle(IReadOnlyList<RunSpec> specs) => Specs = specs;

    public IReadOnlyList<RunSpec> Specs { get; }
    public Task<RunResult> Completion => _completion.Task;
    public DateTimeOffset StartedAt { get; private set; }

    /// <summary>The process currently running (the latest one once finished).</summary>
    public int ProcessId
    {
        get
        {
            lock (_lock)
                return _processes[^1].Id;
        }
    }

    public IReadOnlyList<OutputLine> Output
    {
        get
        {
            lock (_lock)
                return [.. _lines];
        }
    }

    public IDisposable Subscribe(IObserver<OutputLine> observer)
    {
        lock (_lock)
        {
            foreach (var line in _lines)
                observer.OnNext(line);
            if (_outputEnded)
            {
                observer.OnCompleted();
                return Unsubscriber.None;
            }
            _observers.Add(observer);
        }
        return new Unsubscriber(() =>
        {
            lock (_lock)
                _observers.Remove(observer);
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
        StartedProcess current;
        lock (_lock)
        {
            _stopOutcome ??= outcome;
            current = _processes[^1];
        }
        if (!Completion.IsCompleted && grace > TimeSpan.Zero && AskToStop(current, stopCompanion))
            await Task.WhenAny(current.Exited, Task.Delay(grace));

        StartedProcess[] all;
        lock (_lock)
            all = [.. _processes];
        foreach (var process in all)
            process.Job.Terminate(1);
        await Completion;
    }

    private static bool AskToStop(StartedProcess current, Func<bool>? stopCompanion) =>
        stopCompanion?.Invoke() == true || current.Job.CloseWindows() > 0;

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var process in _processes)
                process.Dispose();
        }
        EndOutput();
    }

    internal void Begin()
    {
        StartedAt = DateTimeOffset.Now;
        _clock.Start();
        _processes.Add(ProcessRunner.Launch(Specs[0], Publish));
        _ = RunAsync();
    }

    private async Task RunAsync()
    {
        RunResult result;
        for (var index = 0; ; index++)
        {
            StartedProcess current;
            lock (_lock)
                current = _processes[^1];

            using var timeout = Specs[index].Timeout is { } limit ? new CancellationTokenSource(limit) : null;
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
                    result = new RunResult(stopped, exitCode, _clock.Elapsed);
                    break;
                }
                if (exitCode != 0 || index == Specs.Count - 1)
                {
                    result = new RunResult(RunOutcome.Exited, exitCode, _clock.Elapsed);
                    break;
                }
                try
                {
                    _processes.Add(ProcessRunner.Launch(Specs[index + 1], Publish));
                }
                catch (RunException ex)
                {
                    PublishLocked(ex.Message, OutputStream.Info);
                    result = new RunResult(RunOutcome.FailedToStart, -1, _clock.Elapsed);
                    break;
                }
            }
        }
        _completion.SetResult(result);

        Task[] outputs;
        lock (_lock)
            outputs = [.. _processes.Select(p => p.Output)];
        await Task.WhenAll(outputs);
        EndOutput();
    }

    private void Publish(string text, OutputStream stream)
    {
        lock (_lock)
            PublishLocked(text, stream);
    }

    private void PublishLocked(string text, OutputStream stream)
    {
        var line = new OutputLine(text, stream);
        _lines.Add(line);
        foreach (var observer in _observers)
            observer.OnNext(line);
    }

    private void EndOutput()
    {
        lock (_lock)
        {
            if (_outputEnded)
                return;
            _outputEnded = true;
            foreach (var observer in _observers)
                observer.OnCompleted();
            _observers.Clear();
        }
    }

    private sealed class ActionObserver(Action<OutputLine> onLine) : IObserver<OutputLine>
    {
        public void OnNext(OutputLine value) => onLine(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class Unsubscriber(Action? unsubscribe) : IDisposable
    {
        public static readonly Unsubscriber None = new(null);
        public void Dispose() => unsubscribe?.Invoke();
    }
}
