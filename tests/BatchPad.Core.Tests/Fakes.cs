using System.Collections.Concurrent;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workflows;

namespace BatchPad.Core.Tests;

internal sealed class FakeOpener : IShellOpener
{
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConcurrentQueue<string> Targets { get; } = new();

    public void Open(string target)
    {
        Targets.Enqueue(target);
        _first.TrySetResult();
    }

    public Task FirstOpen => _first.Task;
}

internal sealed class RecordingLauncher : IScheduleLauncher
{
    private readonly List<(object Request, FakeRun Run)> _runs = [];

    public List<(object Request, FakeRun Run)> Runs
    {
        get
        {
            lock (_runs)
                return [.. _runs];
        }
    }

    public IRunOutput Start(RunRequest request) => Add(request);

    public IRunOutput Start(WorkflowRequest request, string trigger) => Add(request);

    private FakeRun Add(object request)
    {
        var run = new FakeRun();
        lock (_runs)
            _runs.Add((request, run));
        return run;
    }
}

internal sealed class FakeRun : IRunOutput
{
    private readonly TaskCompletionSource<RunResult> _completion = new();
    private readonly List<Action<OutputLine>> _subscribers = [];

    public Task<RunResult> Completion => _completion.Task;

    public IDisposable Subscribe(Action<OutputLine> onLine)
    {
        _subscribers.Add(onLine);
        return Disposable.None;
    }

    public void Emit(string text)
    {
        foreach (var subscriber in _subscribers)
            subscriber(new OutputLine(text, OutputStream.Stdout));
    }

    public void Complete(int exitCode) => _completion.SetResult(new RunResult(RunOutcome.Exited, exitCode, TimeSpan.FromSeconds(1)));
}
