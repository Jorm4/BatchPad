using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using BatchPad.App.Services;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

/// <param name="Spans">ANSI-styled runs of <paramref name="Text"/>; null for an unstyled line.</param>
public sealed record OutputLineViewModel(string Text, OutputStream Stream, IReadOnlyList<OutputSpan>? Spans = null, bool IsErrorMatch = false,
    IReadOnlyList<SourceLinkViewModel>? Links = null, LineSeverity Severity = LineSeverity.None)
{
    public static OutputLineViewModel From(ParsedLine line, OutputStream stream, SourceLinks? links = null) =>
        new(line.Text, stream, line.Spans, line.IsErrorMatch, links?.For(line.Text), line.Severity);

    // Identity, so the list can select one of several identical lines.
    public bool Equals(OutputLineViewModel? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);

    public bool IsError => Stream == OutputStream.Stderr;
    public bool IsInfo => Stream == OutputStream.Info;
    public override string ToString() => Text;
}

/// <summary>What a run tab needs beyond the process: the request it ran, for ready, stop companion and artifacts.</summary>
public sealed record RunContext(RunRequest Request, IShellOpener Opener, Func<RunRequest, bool> StartCompanion, SourceOpener? Sources = null,
    Action<string, IReadOnlyDictionary<string, JsonNode?>, string>? RunAgain = null, Action<string>? PinLink = null)
{
    public RunRecording? Recording { get; init; }
}

/// <summary>The history record a run is being saved as, and the store it goes to.</summary>
public sealed record RunRecording(Task<RunRecord> Record, HistoryStore Store);

public sealed partial class SourceLinkViewModel(SourceReference reference, SourceLocationResolver resolver, SourceOpener opener)
{
    public SourceReference Reference { get; } = reference;
    public SourceLocation? Location => resolver.Resolve(Reference);

    [RelayCommand]
    private void Open()
    {
        if (Location is { } location)
            opener.Open(location);
    }
}

/// <summary>Finds the source references of one run's lines; the files are only checked when a link is shown or clicked.</summary>
public sealed class SourceLinks(Func<string> workingDirectory, SourceOpener opener)
{
    private readonly SourceLocationResolver _resolver = new(workingDirectory);

    public IReadOnlyList<SourceLinkViewModel>? For(string text) =>
        SourceLocationParser.Find(text) is { } references ? [.. references.Select(r => new SourceLinkViewModel(r, _resolver, opener))] : null;
}

public sealed partial class ArtifactLinkViewModel(ResolvedArtifact artifact, IShellOpener opener, Action<string>? pinLink = null) : ObservableObject
{
    public ResolvedArtifact Artifact { get; } = artifact;
    public string Name => Path.GetFileName(Artifact.Path.TrimEnd('\\', '/'));

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    private bool exists = artifact.Exists;

    [RelayCommand(CanExecute = nameof(Exists))]
    private void Open() => opener.Open(Artifact.Path);

    public bool CanPin => pinLink is not null;

    [RelayCommand(CanExecute = nameof(CanPin))]
    private void Pin() => pinLink!(Artifact.Path);
}

public sealed partial class RunViewModel : OutputTabViewModel
{
    private readonly IRunProcess? _process;
    private readonly IDisposable? _subscription;
    private readonly RunContext? _context;
    private readonly ReadyWatcher? _readyWatcher;
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;

    public RunViewModel(string title, NodeViewModel? node, IRunProcess process, IUiDispatcher dispatcher, RunContext? context = null)
        : base(title, node)
    {
        Log = new OutputLog(() => AutoScroll = false);
        _process = process;
        _context = context;
        node?.OnRunStarted();
        var secrets = context is null ? [] : SecretMasker.SecretValues(context.Request);
        var parser = new OutputLineParser(context?.Request.Script.ErrorPatterns);
        var links = context?.Sources is { } sources ? new SourceLinks(() => RunPlanner.WorkingDirectoryFor(context.Request), sources) : null;
        var poster = new OutputPoster(Log, dispatcher);
        _subscription = process.Subscribe(line =>
            poster.Add(OutputLineViewModel.From(parser.Parse(SecretMasker.Mask(line.Text, secrets)), line.Stream, links)));
        process.WaitingChanged += () => dispatcher.Post(() => ShowWaiting(process.WaitingForLock));
        ShowWaiting(process.WaitingForLock);
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
        Log = new OutputLog();
        Lines.Add(new OutputLineViewModel(error, OutputStream.Stderr));
        var result = new RunResult(RunOutcome.FailedToStart, -1, TimeSpan.Zero);
        node?.OnRunStarted();
        Complete(result);
        Finished = Task.CompletedTask;
    }

    public static RunViewModel FailedToStart(string title, NodeViewModel? node, string error) => new(title, node, error);

    public override OutputLog Log { get; }
    public ObservableCollection<OutputLineViewModel> Lines => Log.Lines;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? waitingForLock;

    [ObservableProperty]
    private TestResultsViewModel? testResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLines))]
    private bool showTests;

    [ObservableProperty]
    private BenchmarkResultsViewModel? benchmarkResults;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLines))]
    private bool showBenchmarks;

    public bool ShowLines => !ShowTests && !ShowBenchmarks;

    partial void OnShowTestsChanged(bool value)
    {
        if (value)
            ShowBenchmarks = false;
    }

    partial void OnShowBenchmarksChanged(bool value)
    {
        if (value)
            ShowTests = false;
    }

    public bool IsReady => ReachedReady && IsRunning;

    public override bool IsRunning => Result is null;
    public override bool Succeeded => Result?.Succeeded == true;

    public override string StatusText =>
        IsReady ? "running · ready"
        : IsRunning && WaitingForLock is { } name ? $"waiting for lock {name}"
        : StatusOf(Result);

    public static string StatusOf(RunResult? result) => result switch
    {
        null => "running",
        { Outcome: RunOutcome.Exited, ExitCode: var code } => $"exit {code}",
        { Outcome: RunOutcome.Stopped } => "stopped",
        { Outcome: RunOutcome.TimedOut } => "timed out",
        _ => "failed to start",
    };

    public static string StatusOf(RunRecord record) => StatusOf(new RunResult(record.Outcome, record.ExitCode, record.Duration));

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
        var report = ReadTestReport();
        await PostAsync(dispatcher, () =>
        {
            Complete(outcome);
            if (report is not null)
                TestResults = TestResultsFor(report);
        }).ConfigureAwait(false);
        if (_context?.Recording is not { } recording)
            return;
        BenchmarkResultsViewModel? benchmarks;
        try
        {
            benchmarks = await BenchmarksAsync(recording).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Rethrown on the UI thread, where App logs and reports what nothing else handled.
            if (!IoProblems.IsIoProblem(ex))
                dispatcher.Post(() => ExceptionDispatchInfo.Throw(ex));
            return;
        }
        if (benchmarks is not null)
            await PostAsync(dispatcher, () => BenchmarkResults = benchmarks).ConfigureAwait(false);
    }

    private static Task PostAsync(IUiDispatcher dispatcher, Action action)
    {
        var done = new TaskCompletionSource();
        dispatcher.Post(() =>
        {
            action();
            done.SetResult();
        });
        return done.Task;
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

    private JUnitReport? ReadTestReport() => _context is null ? null : TestReportReader.ForFinishedRun(_context.Request, _startedUtc);

    private static async Task<BenchmarkResultsViewModel?> BenchmarksAsync(RunRecording recording)
    {
        var record = await recording.Record.ConfigureAwait(false);
        return record.Benchmarks is { Results.Count: > 0 } ? new BenchmarkResultsViewModel(record, recording.Store.Recent()) : null;
    }

    private TestResultsViewModel TestResultsFor(JUnitReport report)
    {
        var request = _context!.Request;
        var settings = request.Script.TestReport!;
        Action<IReadOnlyList<string>>? rerun = settings.RerunParam is { Length: > 0 } parameter && _context.RunAgain is { } runAgain
            ? names =>
            {
                if (Node is { } node)
                    runAgain(node.Key, TestRerun.Values(request, parameter, names), request.ExtraArguments ?? "");
            }
            : null;
        return new TestResultsViewModel(report, settings.RerunBy ?? RerunBy.Case, rerun);
    }

    private static IReadOnlyList<ArtifactLinkViewModel> ResolveArtifacts(RunContext context)
    {
        try
        {
            return ArtifactResolver.Resolve(context.Request).Select(a => new ArtifactLinkViewModel(a, context.Opener, context.PinLink)).ToList();
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

    private void ShowWaiting(string? lockName)
    {
        if (!IsRunning || WaitingForLock == lockName)
            return;
        WaitingForLock = lockName;
        Node?.OnRunWaiting(lockName);
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
