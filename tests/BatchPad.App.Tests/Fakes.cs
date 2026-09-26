using BatchPad.App.Services;
using BatchPad.Core.Output;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

internal sealed class FakeLauncher : IRunLauncher
{
    public List<FakeProcess> Started { get; } = [];
    public List<RunRequest> Requests { get; } = [];
    public Exception? Failure { get; init; }

    public IRunProcess Start(RunRequest request)
    {
        Requests.Add(request);
        if (Failure is not null)
            throw Failure;
        var process = new FakeProcess();
        Started.Add(process);
        return process;
    }
}

internal sealed class FakeProcess : IRunProcess
{
    private readonly TaskCompletionSource<RunResult> _completion = new();
    private Action<OutputLine>? _onLine;

    public Task<RunResult> Completion => _completion.Task;

    public IDisposable Subscribe(Action<OutputLine> onLine)
    {
        _onLine += onLine;
        return new Subscription(() => _onLine -= onLine);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }

    public void Emit(string text, OutputStream stream) => _onLine?.Invoke(new OutputLine(text, stream));

    public void Finish(RunOutcome outcome, int exitCode) =>
        _completion.TrySetResult(new RunResult(outcome, exitCode, TimeSpan.FromSeconds(1)));

    public bool? CompanionStarted { get; private set; }

    public string? WaitingForLock { get; private set; }

    public event Action? WaitingChanged;

    public void Wait(string? lockName)
    {
        WaitingForLock = lockName;
        WaitingChanged?.Invoke();
    }

    public Task StopAsync(Func<bool>? stopCompanion = null)
    {
        CompanionStarted = stopCompanion?.Invoke();
        Finish(RunOutcome.Stopped, -1);
        return Task.CompletedTask;
    }

    public void Dispose() => _onLine = null;
}

internal sealed class FakeShell : IShellService
{
    public string? Copied { get; private set; }
    public List<string> Opened { get; } = [];

    public void CopyText(string text) => Copied = text;
    public void Open(string target, bool confirmed = false) => Opened.Add(target);
    public List<string> Commands { get; } = [];

    public void RunCommand(string commandLine, IReadOnlyDictionary<string, string>? environment = null) =>
        Commands.Add((environment ?? new Dictionary<string, string>()).Aggregate(commandLine, (line, v) => line.Replace($"!{v.Key}!", v.Value)));
}

internal sealed class FakeDialogs : IFileDialogService
{
    public string? File { get; set; }
    public string? Folder { get; set; }
    public List<string> Asked { get; } = [];

    public string? PickFile(string initialDirectory)
    {
        Asked.Add(initialDirectory);
        return File;
    }

    public string? PickFolder(string initialDirectory)
    {
        Asked.Add(initialDirectory);
        return Folder;
    }
}

internal sealed class FakeConfirm : IConfirmService
{
    public bool Answer { get; set; }
    public List<string> Asked { get; } = [];

    public bool Confirm(string title, string message)
    {
        Asked.Add(message);
        return Answer;
    }

    public bool? CancellableAnswer { get; set; }

    public bool? ConfirmOrCancel(string title, string message)
    {
        Asked.Add(message);
        return CancellableAnswer;
    }
}

internal sealed class FakeTray : ITrayService
{
    public bool IsVisible { get; set; }
    public List<(string Title, string Message, Action OnClick)> Notifications { get; } = [];

    public event Action? OpenRequested;
    public event Action? SchedulesRequested;
    public event Action? ExitRequested;

    public void RaiseOpen() => OpenRequested?.Invoke();
    public void RaiseSchedules() => SchedulesRequested?.Invoke();
    public void RaiseExit() => ExitRequested?.Invoke();

    public void Notify(string title, string message, Action onClick) => Notifications.Add((title, message, onClick));

    public void Dispose()
    {
    }
}

/// <summary>Posts inline but holds background work until <see cref="RunAll"/>, to observe the state before it lands.</summary>
internal sealed class QueuedDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _background = new();

    public void Post(Action action) => action();

    public void Background<T>(Func<T> work, Action<T> apply, TimeSpan delay = default) => _background.Enqueue(() => apply(work()));

    public void RunAll()
    {
        while (_background.TryDequeue(out var next))
            next();
    }
}
