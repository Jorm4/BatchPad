using System.Collections.ObjectModel;
using BatchPad.App.Services;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed record OutputLineViewModel(string Text, OutputStream Stream)
{
    public bool IsError => Stream == OutputStream.Stderr;
    public bool IsInfo => Stream == OutputStream.Info;
    public override string ToString() => Text;
}

/// <summary>What a run tab needs beyond the process: the request it ran, for ready, stop companion and artifacts.</summary>
/// <param name="StartCompanion">Starts a stop companion request and shows it; false when it could not start.</param>
public sealed record RunContext(RunRequest Request, IShellOpener Opener, Func<RunRequest, bool> StartCompanion);

public sealed partial class ArtifactLinkViewModel(ResolvedArtifact artifact, IShellOpener opener) : ObservableObject
{
    public ResolvedArtifact Artifact { get; } = artifact;
    public string Name => Path.GetFileName(Artifact.Path.TrimEnd('\\', '/'));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool exists = artifact.Exists;

    [RelayCommand(CanExecute = nameof(Exists))]
    private void Open() => opener.Open(Artifact.Path);
}

public sealed partial class RunViewModel : OutputTabViewModel
{
    private readonly IRunProcess? _process;
    private readonly IDisposable? _subscription;
    private readonly RunContext? _context;
    private readonly ReadyWatcher? _readyWatcher;

    public RunViewModel(string title, NodeViewModel? node, IRunProcess process, IUiDispatcher dispatcher, RunContext? context = null)
        : base(title, node)
    {
        _process = process;
        _context = context;
        node?.OnRunStarted();
        _subscription = process.Subscribe(line => dispatcher.Post(() => Lines.Add(new OutputLineViewModel(line.Text, line.Stream))));
        if (context is not null)
        {
            Artifacts = ResolveArtifacts(context);
            _readyWatcher = WatchReady(process, context, dispatcher);
        }
        Finished = WatchAsync(process, dispatcher);
    }

    private RunViewModel(string title, NodeViewModel? node, string error)
        : base(title, node)
    {
        Lines.Add(new OutputLineViewModel(error, OutputStream.Stderr));
        var result = new RunResult(RunOutcome.FailedToStart, -1, TimeSpan.Zero);
        node?.OnRunStarted();
        Complete(result);
        Finished = Task.CompletedTask;
    }

    public static RunViewModel FailedToStart(string title, NodeViewModel? node, string error) => new(title, node, error);

    public ObservableCollection<OutputLineViewModel> Lines { get; } = [];
    public IReadOnlyList<ArtifactLinkViewModel> Artifacts { get; } = [];
    public bool HasArtifacts => Artifacts.Count > 0;

    public bool IsLongRunning => _context?.Request.Script is { LongRunning: true } or { Ready.Pattern: not null };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(StatusText), nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private RunResult? result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsReady))]
    private bool reachedReady;

    /// <summary>The expanded <c>ready.open</c> URL once the run is ready.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenInBrowserCommand))]
    private string? readyUrl;

    public bool IsReady => ReachedReady && IsRunning;

    public override bool IsRunning => Result is null;
    public override bool Succeeded => Result?.Succeeded == true;

    public override string StatusText => IsReady ? "running · ready" : StatusOf(Result);

    public static string StatusOf(RunResult? result) => result switch
    {
        null => "running",
        { Outcome: RunOutcome.Exited, ExitCode: var code } => $"exit {code}",
        { Outcome: RunOutcome.Stopped } => "stopped",
        { Outcome: RunOutcome.TimedOut } => "timed out",
        _ => "failed to start",
    };

    public override string DurationText => Result is { Outcome: not RunOutcome.FailedToStart, Duration: var duration }
        ? FormatDuration(duration)
        : "";

    private bool CanOpenInBrowser() => ReadyUrl is not null;

    [RelayCommand(CanExecute = nameof(CanOpenInBrowser))]
    private void OpenInBrowser() => _context?.Opener.Open(ReadyUrl!);

    protected override Task StopRunAsync()
    {
        if (_process is null)
            return Task.CompletedTask;
        var companion = _context is null ? null : StopCoordinator.CompanionFor(_context.Request);
        return _process.StopAsync(companion is null ? null : () => _context!.StartCompanion(companion));
    }

    public override void Dispose()
    {
        _readyWatcher?.Dispose();
        _subscription?.Dispose();
        _process?.Dispose();
    }

    private async Task WatchAsync(IRunProcess process, IUiDispatcher dispatcher)
    {
        var outcome = await process.Completion.ConfigureAwait(false);
        var shown = new TaskCompletionSource();
        dispatcher.Post(() =>
        {
            Complete(outcome);
            shown.SetResult();
        });
        await shown.Task.ConfigureAwait(false);
    }

    private void Complete(RunResult outcome)
    {
        Result = outcome;
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(Succeeded));
        Node?.OnRunFinished(outcome);
        if (_context is null)
            return;
        foreach (var link in Artifacts)
            link.Exists = link.Artifact.Exists;
        ArtifactResolver.OpenAfter(outcome, Artifacts.Select(a => a.Artifact), _context.Opener);
    }

    private static IReadOnlyList<ArtifactLinkViewModel> ResolveArtifacts(RunContext context)
    {
        try
        {
            return ArtifactResolver.Resolve(context.Request).Select(a => new ArtifactLinkViewModel(a, context.Opener)).ToList();
        }
        catch (TemplateException)
        {
            return [];
        }
    }

    private ReadyWatcher? WatchReady(IRunProcess process, RunContext context, IUiDispatcher dispatcher)
    {
        if (!IsLongRunning)
            return null;
        try
        {
            var watcher = ReadyWatcher.Watch(process, context.Request, context.Opener);
            _ = watcher.Ready.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && t.Result is { } signal)
                    dispatcher.Post(() => MarkReady(signal));
            }, TaskScheduler.Default);
            return watcher;
        }
        catch (TemplateException)
        {
            return null;
        }
    }

    private void MarkReady(ReadySignal signal)
    {
        if (!IsRunning)
            return;
        ReadyUrl = signal.Url;
        ReachedReady = true;
        Node?.OnRunReady();
    }
}
