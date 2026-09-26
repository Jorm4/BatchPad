using System.Collections.ObjectModel;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.History;
using BatchPad.App.ViewModels.AppSettings;
using BatchPad.App.ViewModels.Insights;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Schedules;
using BatchPad.App.ViewModels.Wizard;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.App.ViewModels.Workspace;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BatchPad.Core.Choices;
using BatchPad.Core.Detection;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed record WorkspaceChoice(string FilePath)
{
    public string DisplayName => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? FilePath;
}

public enum CloseAction { Close, Cancel, StopThenClose, HideToTray }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private readonly RunningRegistry _running;
    private bool _opening;
    private bool _watchFiles;
    private static readonly string[] NoiseFolders = [".git", "bin", "obj", "node_modules"];

    private FolderWatcher? _watcher;
    private WorkspaceFingerprint? _fingerprint;
    private readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, string Hash)> _configHashes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Debouncer _fingerprints;
    private readonly Dictionary<string, DateTime> _scriptStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScheduleStateStore _scheduleState;
    private string? _schedulerWorkspaceId;
    private IDisposable? _eventTriggers;
    private FolderWatcher? _scheduleWatcher;
    private FileChangedTriggerSource? _fileTriggers;

    public MainViewModel(AppPaths paths, Settings settings,
        IRunLauncher? launcher = null, IUiDispatcher? dispatcher = null, IShellService? shell = null, IFileDialogService? dialogs = null,
        IConfirmService? confirm = null, IWorkflowLauncher? workflows = null, TimeProvider? time = null, IAskService? ask = null,
        ITrayService? tray = null)
    {
        _paths = paths;
        _settings = settings;
        _running = RunningRegistry.For(paths);
        Time = time ?? TimeProvider.System;
        Trust = new TrustStore(settings, paths.SettingsFile);
        var interpreters = new InterpreterLocator(settings.Interpreters);
        var gate = new RunGate(Trust, Time, LockManager.For(paths));
        CommandChoices = new CommandChoiceSource(Trust, interpreters);
        Probes = new ScriptProbes(Trust, interpreters);
        shell ??= new ShellService();
        Services = new AppServices(launcher ?? new GatedRunLauncher(gate, interpreters), interpreters,
            dispatcher ?? new ImmediateDispatcher(), shell, dialogs ?? new FileDialogService(),
            confirm ?? new MessageBoxConfirmService(), workflows ?? new GatedWorkflowLauncher(gate, interpreters, new ShellOpener(shell)),
            ask ?? new AskDialogService());
        Sources = new SourceOpener(shell, settings);
        _fingerprints = new Debouncer(Services.Dispatcher, TimeSpan.Zero);
        _scheduleState = ScheduleStateStore.For(paths);
        Details = new DetailsViewModel(this);
        Telemetry = TelemetryPipeline.For(paths, settings, Time);
        Telemetry.StatusChanged += () => Services.Dispatcher.Post(RefreshTelemetryProblem);
        RefreshTelemetryProblem();
        Telemetry.Start();
        History = new HistoryViewModel(this);
        Output = new OutputPanelViewModel(History);
        MyScripts = new MyScriptsViewModel(this);
        Palette = new CommandPaletteViewModel(this);
        Tray = tray;
        if (tray is not null)
        {
            tray.OpenRequested += ShowWindow;
            tray.SchedulesRequested += () =>
            {
                ShowWindow();
                OpenSchedulesCommand.Execute(null);
            };
            tray.ExitRequested += Exit;
        }
        RefreshRecents();
    }

    public TrustStore Trust { get; }
    public CommandChoiceSource CommandChoices { get; }
    public ScriptProbes Probes { get; }
    public AppServices Services { get; }
    public SourceOpener Sources { get; }
    public AppPaths Paths => _paths;
    public TimeProvider Time { get; }
    public HistoryViewModel History { get; }
    public TelemetryPipeline Telemetry { get; }
    public Settings UserSettings => _settings;
    public RenameTracker Renames { get; } = new();
    public Scheduler? Scheduler { get; private set; }

    public ITrayService? Tray { get; }
    public bool IsInTray { get; private set; }
    public event Action? ShowWindowRequested;
    public event Action? ExitRequested;

    public bool KeepRunningInTray
    {
        get => _settings.KeepRunningInTray;
        set
        {
            if (_settings.KeepRunningInTray == value)
                return;
            _settings.Update(_paths.SettingsFile, s => s.KeepRunningInTray = value);
            OnPropertyChanged();
        }
    }

    private bool HasEnabledSchedules => Scheduler?.Statuses().Any(s => s.Entry.Schedule.Enabled) == true;

    public Dictionary<string, ParameterValues> SessionValues { get; } = [];
    public DetailsViewModel Details { get; }
    public MyScriptsViewModel MyScripts { get; }
    public CommandPaletteViewModel Palette { get; }
    public OutputPanelViewModel Output { get; }
    public ObservableCollection<WorkspaceChoice> RecentWorkspaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkspaceSettingsCommand), nameof(OpenSchedulesCommand), nameof(OpenInsightsCommand))]
    private LoadedWorkspace? workspace;

    [ObservableProperty]
    private TreeViewModel? tree;

    [ObservableProperty]
    private WorkspaceChoice? selectedWorkspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrustPromptVisible))]
    private bool isTrusted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTrustPromptVisible))]
    private bool isTrustPromptDismissed;

    [ObservableProperty]
    private string? loadErrors;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen))]
    private WorkspaceSettingsViewModel? workspaceSettings;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen))]
    private NewItemViewModel? newItem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen))]
    private NewWorkspaceWizardViewModel? newWorkspace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen))]
    private SchedulesViewModel? schedules;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen))]
    private InsightsViewModel? insights;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPageOpen))]
    private SettingsViewModel? settingsPage;

    /// <summary>A page replaces the details panel while it is open.</summary>
    public bool IsPageOpen =>
        WorkspaceSettings is not null || NewItem is not null || NewWorkspace is not null || Schedules is not null || Insights is not null
        || SettingsPage is not null;

    private bool IsEditingPage => WorkspaceSettings is not null || NewItem is not null || NewWorkspace is not null || Schedules?.Editor is not null;

    [ObservableProperty]
    private bool isReloadPromptVisible;

    [ObservableProperty]
    private string? telemetryProblem;

    // Holds until every sink recovers, so a sink that keeps failing doesn't reopen the banner on each retry.
    private bool _telemetryProblemDismissed;

    public WindowLayout? WindowLayout => _settings.Window;

    public bool IsTrustPromptVisible => Workspace is not null && !IsTrusted && !IsTrustPromptDismissed;

    public string? TrustPromptText => Workspace is null ? null : new RunGate(Trust).Check(Workspace).Reason;

    public NodeViewModel? SelectedNode => Tree?.SelectedNode;

    /// <summary>Opens the command-line workspace, else the nearest or most recent one. False when there is none.</summary>
    public bool OpenInitial(string? commandLinePath, string currentDirectory)
    {
        var file = WorkspaceLocator.Locate(commandLinePath, currentDirectory, _settings.RecentWorkspaces);
        if (file is null)
            return false;
        Open(file);
        return true;
    }

    public void Open(string workspaceFile)
    {
        var loaded = WorkspaceLoader.Load(workspaceFile, _paths, Trust);
        var previous = Tree is not null && string.Equals(Workspace?.FilePath, loaded.FilePath, StringComparison.OrdinalIgnoreCase) ? Tree : null;
        if (Tree is not null)
            Tree.PropertyChanged -= OnTreePropertyChanged;

        Workspace = loaded;
        ClosePages(keepSchedules: true, keepInsights: true, keepSettings: true);
        Tree = new TreeViewModel(loaded, OpenNewItem);
        Tree.PropertyChanged += OnTreePropertyChanged;
        IsTrusted = Trust.IsTrusted(loaded.Directory);
        IsTrustPromptDismissed = false;
        LoadErrors = loaded.Errors.Count == 0 ? null : string.Join(Environment.NewLine, loaded.Errors.Select(e => $"{e.FilePath}: {e.Message}"));
        OnPropertyChanged(nameof(TrustPromptText));
        OnPropertyChanged(nameof(SelectedNode));
        Details.Node = null;
        History.Show(loaded);
        ShowLastResults(Tree);
        var schedules = ScheduleEntry.For(loaded);
        StartScheduler(loaded, schedules);
        ShowScheduleBadges(Tree, schedules);
        Schedules?.Refresh(schedules);
        Tree.RefreshLinks(Time);
        if (previous is not null)
            CarryRunState(previous, Tree);
        RecordSeenBaseline(loaded);
        AdoptRuns(loaded);
        IsReloadPromptVisible = false;
        _fingerprint = null;
        _fingerprints.Run(() => FingerprintOf(loaded), now =>
        {
            if (ReferenceEquals(Workspace, loaded))
                _fingerprint ??= now;
        });
        Watch();
        ScanProposals();

        if (File.Exists(loaded.FilePath))
        {
            _settings.Update(_paths.SettingsFile, s => s.AddRecentWorkspace(loaded.FilePath));
        }
        RefreshRecents();
    }

    public void Reload(Func<NodeViewModel, bool>? select = null)
    {
        if (Workspace is null)
            return;
        Open(Workspace.FilePath);
        if (select is not null && Tree!.AllNodes.FirstOrDefault(select) is { } node)
            node.Reveal();
    }

    public void CheckForExternalChanges()
    {
        if (Workspace is not { } workspace)
            return;
        IoProblems.TryIo(() => Renames.Apply(workspace));
        _fingerprints.Run(() => FingerprintOf(workspace), now =>
        {
            if (ReferenceEquals(Workspace, workspace) && now is not null)
                OnFingerprint(now);
        });
    }

    private void OnFingerprint(WorkspaceFingerprint now)
    {
        if (_fingerprint is null || now == _fingerprint)
        {
            _fingerprint = now;
            Tree?.RefreshLinks(Time);
            ScanProposals();
            return;
        }
        _fingerprint = now;
        if (Details.Editor is { } editor && !IsEditingPage)
        {
            if (editor.PendingEditStillApplies())
                ReloadKeepingEditor(editor);
            else
                IsReloadPromptVisible = true;
        }
        else if (IsEditingPage || Details.IsEditing)
            IsReloadPromptVisible = true;
        else
            ReloadKeepingSelection();
    }

    public Task ProposalScan { get; private set; } = Task.CompletedTask;

    /// <summary>Probes run only for files changed since the last scan, so opening a workspace starts no process.</summary>
    public void ScanProposals()
    {
        if (Tree is null || Workspace is null)
            return;
        var dismissed = LoadDismissed();
        var targets = Tree.AllNodes
            .Where(n => n is { ProposalKey: not null, Script: not null })
            .Select(n => (Node: n, Entry: n.Script!, Path: n.ScriptFullPath!, Dismissed: dismissed.GetValueOrDefault(n.ProposalKey!) ?? []))
            .ToList();
        ProposalScan = ProposalScan.ContinueWith(_ =>
        {
            foreach (var (node, entry, path, dismissedKeys) in targets)
            {
                string? badge;
                try
                {
                    var stamp = File.GetLastWriteTimeUtc(path);
                    var changed = _scriptStamps.TryGetValue(path, out var previous) && previous != stamp;
                    _scriptStamps[path] = stamp;
                    badge = ProposalTracker.For(entry, path, dismissedKeys, Probes, cachedProbesOnly: !changed).Badge;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    badge = null;
                }
                Services.Dispatcher.Post(() => node.ProposalBadge = badge);
            }
        }, TaskScheduler.Default);
    }

    public IReadOnlyCollection<string> DismissedProposals(NodeViewModel node) =>
        node.ProposalKey is { } key ? LoadDismissed().GetValueOrDefault(key) ?? [] : [];

    private Dictionary<string, List<string>> LoadDismissed()
    {
        try
        {
            return Workspace is null ? [] : UserStore.For(Workspace).Load().DismissedProposals ?? [];
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            return [];
        }
    }

    public void DismissProposal(NodeViewModel node, string proposalKey)
    {
        if (Workspace is not { } workspace || node.ProposalKey is not { } scriptKey)
            return;
        IoProblems.TryIo(() =>
        {
            UserStore.For(workspace).DismissProposal(scriptKey, proposalKey);
            RefreshConfigFingerprint(workspace);
        });
        ScanProposals();
    }

    public void EnableFileWatching()
    {
        _watchFiles = true;
        Watch();
        if (Workspace is { } workspace && Scheduler is { } scheduler && WatchScheduledFiles(workspace))
            scheduler.Update(ScheduleEntry.For(workspace));
    }

    /// <param name="exiting">True for the tray's Exit, which never hides to the tray.</param>
    public CloseAction ConfirmClose(bool exiting = false)
    {
        if (!exiting && Tray is not null && KeepRunningInTray && HasEnabledSchedules)
        {
            IsInTray = true;
            UpdateTray();
            return CloseAction.HideToTray;
        }
        var running = Output.Tabs.Count(t => t.IsRunning);
        if (running == 0)
            return CloseAction.Close;
        var (runs, them) = running == 1 ? ("A run is", "it") : ($"{running} runs are", "them");
        var message = $"{runs} still running. Stop {them} before closing?\n\n"
            + $"Yes stops {them}. No leaves {them} running, and long-running ones come back when BatchPad opens again. "
            + "Cancel keeps BatchPad open.";
        return Services.Confirm.ConfirmOrCancel("Close BatchPad", message) switch
        {
            true => CloseAction.StopThenClose,
            false => CloseAction.Close,
            null => CloseAction.Cancel,
        };
    }

    public void ShowWindow()
    {
        IsInTray = false;
        UpdateTray();
        ShowWindowRequested?.Invoke();
    }

    /// <summary>Delivers what telemetry it can within <see cref="TelemetryPipeline.ExitFlushLimit"/>, then stops it.</summary>
    public void Shutdown()
    {
        var stopping = Task.Run(async () =>
        {
            await Telemetry.FlushAsync(TelemetryPipeline.ExitFlushLimit).ConfigureAwait(false);
            await Telemetry.DisposeAsync().ConfigureAwait(false);
        });
        try
        {
            stopping.Wait(TelemetryPipeline.ExitFlushLimit);
        }
        catch (AggregateException)
        {
        }
    }

    private void Exit()
    {
        if (Output.Tabs.Any(t => t.IsRunning))
            ShowWindow();
        ExitRequested?.Invoke();
    }

    private void UpdateTray()
    {
        if (Tray is not null)
            Tray.IsVisible = IsInTray || HasEnabledSchedules;
    }

    private void NotifyFailure(ScheduleFailure failure)
    {
        var name = failure.Entry.Target?.Name ?? failure.Entry.Schedule.Target;
        Tray?.Notify($"Scheduled run failed: {name}", failure.Message, () => ShowFailure(failure));
    }

    public void ShowFailure(ScheduleFailure failure)
    {
        ShowWindow();
        if (failure.Record is { } record && History.Select(record.Id))
            Output.IsHistoryOpen = true;
        else
            OpenSchedulesCommand.Execute(null);
    }

    public Task StopAllAsync() => Task.WhenAll(Output.Tabs.Where(t => t.IsRunning).ToList().Select(t => t.StopCommand.ExecuteAsync(null)));

    public void TrackLongRunning(IRunProcess process, RunRequest request, NodeViewModel? node)
    {
        if (request.Script.LongRunning != true || node is null || Workspace is not { } workspace || process.ProcessId is not { } processId)
            return;
        var secrets = SecretMasker.SecretNames(request);
        IoProblems.TryIo(() =>
        {
            var entry = _running.Add(new RunningEntry
            {
                WorkspaceFile = workspace.FilePath,
                NodeKey = node.Key,
                Name = node.Name,
                ProcessId = processId,
                Values = (request.Values ?? new Dictionary<string, JsonNode?>())
                    .Where(v => !secrets.Contains(v.Key))
                    .ToDictionary(v => v.Key, v => v.Value?.DeepClone()),
                ExtraArguments = request.ExtraArguments,
            });
            if (entry is not null)
                _ = process.Completion.ContinueWith(_ => IoProblems.TryIo(() => _running.Remove(entry)), TaskScheduler.Default);
        });
    }

    private void AdoptRuns(LoadedWorkspace loaded)
    {
        IReadOnlyList<AdoptedRun> adopted;
        try
        {
            adopted = _running.Adopt(loaded.FilePath);
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            return;
        }
        foreach (var run in adopted)
        {
            var node = Tree!.ByKey(run.Entry.NodeKey);
            var context = node is not null && RequestFor(node, loaded) is { } request
                ? Details.ContextFor(request with { Values = run.Entry.Values, ExtraArguments = run.Entry.ExtraArguments })
                : null;
            Output.Add(new RunViewModel(node?.Name ?? run.Entry.Name, node, new AdoptedProcess(run), Services.Dispatcher, context));
        }
    }

    private static RunRequest? RequestFor(NodeViewModel node, LoadedWorkspace workspace) => node.Customisation is { } customisation
        ? customisation is { IsBroken: false, Definition: ScriptNode } ? customisation.ToRunRequest(workspace) : null
        : node.Node is ScriptNode script ? new RunRequest(workspace, node.Tree, script) : null;

    public void SaveWindowLayout(WindowLayout layout)
    {
        _settings.Update(_paths.SettingsFile, s => s.Window = layout);
    }

    [RelayCommand]
    private void ReloadFromDisk()
    {
        Details.LeaveEditMode();
        ReloadKeepingSelection();
    }

    [RelayCommand]
    private void DismissReloadPrompt() => IsReloadPromptVisible = false;

    [RelayCommand]
    private void DismissTelemetryProblem()
    {
        _telemetryProblemDismissed = true;
        TelemetryProblem = null;
    }

    public void RefreshTelemetryProblem()
    {
        var enabled = _settings.Telemetry?.Sinks.Where(s => s.Enabled).Select(s => s.Key).ToHashSet() ?? [];
        var failing = Telemetry.Statuses().Where(s => s.IsFailing && enabled.Contains(s.Key)).ToList();
        if (failing.Count == 0)
            _telemetryProblemDismissed = false;
        TelemetryProblem = failing.Count == 0 || _telemetryProblemDismissed ? null : DescribeTelemetryProblem(failing);
    }

    private static string DescribeTelemetryProblem(IReadOnlyList<SinkStatus> failing)
    {
        var pending = failing.Sum(s => s.Pending);
        var waiting = pending == 0 ? "" : $" {pending} {(pending == 1 ? "event is" : "events are")} waiting.";
        if (failing is [var only])
            return $"Telemetry to {(only.Target.Length > 0 ? only.Target : only.Type)} is failing: {only.LastError}{waiting}";
        return $"Telemetry to {failing.Count} destinations is failing.{waiting}";
    }

    [RelayCommand]
    private void Escape()
    {
        if (Palette.IsOpen)
            Palette.IsOpen = false;
        else if (Schedules is { Editor: not null } page)
            page.Editor = null;
        else if (IsPageOpen)
            ClosePages();
        else
            Details.LeaveEditMode();
    }

    private void ReloadKeepingSelection()
    {
        var key = SelectedNode?.Key;
        Reload(key is null ? null : n => n.Key == key);
    }

    private void ReloadKeepingEditor(Editor.ScriptEditorViewModel editor)
    {
        var key = editor.Node.Key;
        Reload(n => n.Key == key);
        if (SelectedNode?.Key == key)
            Details.ResumeEditing(editor);
    }

    private void ShowLastResults(TreeViewModel tree)
    {
        var last = History.Store!.LastResults();
        foreach (var node in tree.AllNodes)
            if (node.IsRunnable && last.TryGetValue(node.Key, out var record))
                node.ShowLastResult(record.Succeeded);
    }

    public void RunAgain(string nodeKey, IReadOnlyDictionary<string, JsonNode?> values, string extraArguments)
    {
        if (Tree?.ByKey(nodeKey) is not { } node)
            return;
        SessionValues[nodeKey] = new ParameterValues(values, extraArguments);
        node.Reveal();
        Details.RunWithSessionValues(node);
    }

    private void CarryRunState(TreeViewModel previous, TreeViewModel current)
    {
        var previousNodes = previous.AllNodes.ToHashSet();
        foreach (var old in previousNodes.Where(n => n.Badge != RunBadge.None))
            current.ByKey(old.Key)?.AdoptRunState(old);
        foreach (var tab in Output.Tabs)
            if (tab.Node is { } old && previousNodes.Contains(old) && current.ByKey(old.Key) is { } match)
                tab.Node = match;
    }

    /// <summary>Without <c>seenPaths</c> nothing shows as New, so the first open records every current script as seen (§3.10).</summary>
    private static void RecordSeenBaseline(LoadedWorkspace loaded) => IoProblems.TryIo(() =>
    {
        var store = UserStore.For(loaded);
        if (store.Load().SeenPaths is null)
            store.MarkSeen(ScriptFolderScanner.Scan(loaded.Workspace.BaseDirectory, loaded.Workspace.ScriptFolders).Select(s => s.RelativePath));
    });

    private void MarkSeen(NodeViewModel node)
    {
        node.IsNew = false;
        if (Workspace is not { } workspace || node.Item?.ScriptPath is not { } path)
            return;
        IoProblems.TryIo(() =>
        {
            UserStore.For(workspace).MarkSeen([path]);
            RefreshConfigFingerprint(workspace);
        });
    }

    private sealed record WorkspaceFingerprint(string Configs, string Scripts);

    /// <summary>Null while a file is being written.</summary>
    private WorkspaceFingerprint? FingerprintOf(LoadedWorkspace workspace)
    {
        try
        {
            var scripts = workspace.AllTrees.SelectMany(t => ScriptFolderScanner.Scan(t.BaseDirectory, t.ScriptFolders))
                .Select(s => s.FullPath).Order(StringComparer.OrdinalIgnoreCase);
            return new WorkspaceFingerprint(ConfigsHash(workspace), string.Join('\n', scripts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RefreshConfigFingerprint(LoadedWorkspace workspace)
    {
        if (_fingerprint is not null)
            _fingerprint = _fingerprint with { Configs = ConfigsHash(workspace) };
    }

    private string ConfigsHash(LoadedWorkspace workspace) => string.Join('\n', workspace.AllTrees.Select(t => HashOf(t.FilePath)));

    /// <summary>A file's hash, reused while its size and time match; not just after a write, since file times are coarse.</summary>
    private string HashOf(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
            return "-";
        var stamp = file.LastWriteTimeUtc;
        if (_configHashes.TryGetValue(path, out var known) && known.Stamp == stamp && known.Length == file.Length
            && DateTime.UtcNow - stamp > TimeSpan.FromSeconds(2))
            return known.Hash;
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        _configHashes[path] = (stamp, file.Length, hash);
        return hash;
    }

    private bool IsNoise(string root, string path) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(_paths.DataDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(part => NoiseFolders.Contains(part, StringComparer.OrdinalIgnoreCase));

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!_watchFiles || Workspace is null)
            return;
        _watcher = new FolderWatcher(Workspace.ConfigDirectories.Concat(Workspace.ScriptDirectories), TimeSpan.FromMilliseconds(500), IsNoise);
        _watcher.FileChanged += (_, change) => Renames.Observe(change);
        _watcher.FolderChanged += (_, _) => Services.Dispatcher.Post(CheckForExternalChanges);
    }

    private bool HasWorkspace() => Workspace is not null;

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private void OpenWorkspaceSettings()
    {
        ClosePages();
        var settings = new WorkspaceSettingsViewModel(this);
        settings.Closed += () => WorkspaceSettings = null;
        WorkspaceSettings = settings;
    }

    [RelayCommand]
    private void OpenNewWorkspace()
    {
        ClosePages();
        var wizard = new NewWorkspaceWizardViewModel(this);
        wizard.Closed += () => NewWorkspace = null;
        NewWorkspace = wizard;
    }

    private void OpenNewItem(NodeViewModel near, NewItemKind kind)
    {
        ClosePages();
        if (kind == NewItemKind.Workflow)
        {
            Details.OpenWorkflowEditor(new WorkflowEditorViewModel(this, near.Tree, null, NewItemViewModel.NearestFolder(near)));
            return;
        }
        var item = new NewItemViewModel(this, near, kind);
        item.Closed += () => NewItem = null;
        NewItem = item;
    }

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private void OpenSchedules()
    {
        ClosePages(keepSchedules: true);
        if (Schedules is not null)
            return;
        var page = new SchedulesViewModel(this);
        page.Closed += () => Schedules = null;
        Schedules = page;
    }

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private void OpenInsights()
    {
        ClosePages(keepInsights: true);
        if (Insights is not null)
            return;
        var page = new InsightsViewModel(this);
        page.Closed += () => Insights = null;
        Insights = page;
    }

    [RelayCommand]
    private void OpenSettings()
    {
        ClosePages(keepSettings: true);
        if (SettingsPage is not null)
            return;
        var page = new SettingsViewModel(this);
        page.Closed += () => SettingsPage = null;
        SettingsPage = page;
    }

    private void ClosePages(bool keepSchedules = false, bool keepInsights = false, bool keepSettings = false)
    {
        WorkspaceSettings = null;
        NewItem = null;
        NewWorkspace = null;
        if (!keepSchedules)
            Schedules = null;
        if (!keepInsights)
            Insights = null;
        if (!keepSettings)
            SettingsPage = null;
    }

    partial void OnSchedulesChanged(SchedulesViewModel? oldValue, SchedulesViewModel? newValue) => oldValue?.Detach();

    partial void OnInsightsChanged(InsightsViewModel? oldValue, InsightsViewModel? newValue) => oldValue?.Detach();

    partial void OnSettingsPageChanged(SettingsViewModel? oldValue, SettingsViewModel? newValue)
    {
        oldValue?.Detach();
        RefreshTelemetryProblem();
    }

    private void StartScheduler(LoadedWorkspace loaded, IReadOnlyList<ScheduleEntry> entries)
    {
        if (Scheduler is not null && _schedulerWorkspaceId == loaded.Id)
        {
            Scheduler.Update(entries);
            UpdateTray();
            return;
        }
        StopScheduler();
        var scheduler = new Scheduler(History.Store!, new AppScheduleLauncher(Services.Launcher, Services.Workflows, () => Workspace!, History),
            _scheduleState, Time);
        scheduler.ScheduleFired += fire => Services.Dispatcher.Post(() => ShowScheduledRun(fire));
        scheduler.ScheduleFailed += failure => Services.Dispatcher.Post(() =>
        {
            Schedules?.ShowFailure(failure);
            NotifyFailure(failure);
        });
        scheduler.SchedulePaused += _ => Services.Dispatcher.Post(() => Schedules?.RefreshStatus());
        _eventTriggers = EventTriggers.Register(scheduler, loaded, null);
        Scheduler = scheduler;
        _schedulerWorkspaceId = loaded.Id;
        WatchScheduledFiles(loaded);
        scheduler.Start(entries);
        UpdateTray();
    }

    /// <summary>Adds the <c>fileChanged</c> source once file watching is on; the app enables it after the first open.</summary>
    private bool WatchScheduledFiles(LoadedWorkspace loaded)
    {
        if (!_watchFiles || Scheduler is not { } scheduler || _scheduleWatcher is not null)
            return false;
        _scheduleWatcher = new FolderWatcher([loaded.Directory], TimeSpan.FromMilliseconds(500));
        _fileTriggers = new FileChangedTriggerSource(_scheduleWatcher, loaded.Directory, scheduler.Time);
        scheduler.AddSource(_fileTriggers);
        return true;
    }

    private void StopScheduler()
    {
        _eventTriggers?.Dispose();
        _fileTriggers?.Dispose();
        _scheduleWatcher?.Dispose();
        Scheduler?.Dispose();
        (_eventTriggers, _fileTriggers, _scheduleWatcher, Scheduler) = (null, null, null, null);
    }

    private void ShowScheduledRun(ScheduleFire fire)
    {
        var node = Tree?.ByKey(fire.Entry.Target?.NodeKey);
        var title = fire.Entry.Target?.Name ?? fire.Entry.Schedule.Target;
        OutputTabViewModel? tab = fire.Run switch
        {
            IRunProcess process => new RunViewModel(title, node, process, Services.Dispatcher,
                fire.Request is { } request ? Details.ContextFor(request) : null),
            WorkflowRunOutput workflow when fire.Entry.Target is { } target => Details.WorkflowTab(title, node, workflow.Run, target.Tree),
            _ => null,
        };
        if (tab is not null)
            Output.Add(tab);
        Schedules?.RefreshStatus();
    }

    private static void ShowScheduleBadges(TreeViewModel tree, IReadOnlyList<ScheduleEntry> entries)
    {
        var triggers = entries.Where(e => e.Target is not null).ToLookup(e => e.Target!.NodeKey, e => TriggerText.Describe(e.Schedule.Trigger));
        foreach (var node in tree.AllNodes.Where(n => n.IsRunnable))
            node.ScheduleText = triggers.Contains(node.Key) ? string.Join(Environment.NewLine, triggers[node.Key]) : null;
    }

    [RelayCommand]
    private void TrustWorkspace()
    {
        if (Workspace is null)
            return;
        Trust.Trust(Workspace.Directory);
        IsTrusted = true;
        var selected = SelectedNode?.Key;
        Reload(selected is null ? null : n => n.Key == selected);
    }

    partial void OnIsTrustedChanged(bool value) => Details.Refresh();

    [RelayCommand]
    private void DismissTrustPrompt() => IsTrustPromptDismissed = true;

    partial void OnSelectedWorkspaceChanged(WorkspaceChoice? value)
    {
        if (!_opening && value is not null && !string.Equals(value.FilePath, Workspace?.FilePath, StringComparison.OrdinalIgnoreCase))
            Open(value.FilePath);
    }

    private void RefreshRecents()
    {
        _opening = true;
        try
        {
            RecentWorkspaces.Clear();
            foreach (var file in _settings.RecentWorkspaces)
                RecentWorkspaces.Add(new WorkspaceChoice(file));
            SelectedWorkspace = RecentWorkspaces.FirstOrDefault(r =>
                string.Equals(r.FilePath, Workspace?.FilePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _opening = false;
        }
    }

    private void OnTreePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TreeViewModel.SelectedNode))
            return;
        OnPropertyChanged(nameof(SelectedNode));
        Details.Node = SelectedNode;
        if (SelectedNode is { IsNew: true } node)
            MarkSeen(node);
    }
}
