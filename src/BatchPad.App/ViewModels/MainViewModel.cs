using System.Collections.ObjectModel;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.History;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Schedules;
using BatchPad.App.ViewModels.Wizard;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.App.ViewModels.Workspace;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BatchPad.Core.Choices;
using BatchPad.Core.Detection;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
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
    private FolderWatcher? _watcher;
    private string? _fingerprint;
    private readonly Dictionary<string, DateTime> _scriptStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ScheduleStateStore _scheduleState;
    private string? _schedulerWorkspaceId;
    private IDisposable? _eventTriggers;
    private FolderWatcher? _scheduleWatcher;
    private FileChangedTriggerSource? _fileTriggers;

    /// <param name="launcher">Null starts runs through the real <see cref="RunGate"/>.</param>
    /// <param name="dispatcher">Null runs output callbacks inline, which suits tests only.</param>
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
        var gate = new RunGate(Trust, Time);
        CommandChoices = new CommandChoiceSource(Trust, interpreters);
        Probes = new ScriptProbes(Trust, interpreters);
        shell ??= new ShellService();
        Services = new AppServices(launcher ?? new GatedRunLauncher(gate, interpreters), interpreters,
            dispatcher ?? new ImmediateDispatcher(), shell, dialogs ?? new FileDialogService(),
            confirm ?? new MessageBoxConfirmService(), workflows ?? new GatedWorkflowLauncher(gate, interpreters, new ShellOpener(shell)),
            ask ?? new AskDialogService());
        Sources = new SourceOpener(shell, settings);
        _scheduleState = ScheduleStateStore.For(paths);
        Details = new DetailsViewModel(this);
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
    public RenameTracker Renames { get; } = new();
    public Scheduler? Scheduler { get; private set; }

    /// <summary>Raised on the UI thread when a scheduled run fails or cannot start.</summary>
    public event Action<ScheduleFailure>? ScheduleFailed;

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
            _settings.KeepRunningInTray = value;
            _settings.Save(_paths.SettingsFile);
            OnPropertyChanged();
        }
    }

    private bool HasEnabledSchedules => Scheduler?.Statuses().Any(s => s.Entry.Schedule.Enabled) == true;

    /// <summary>Form values per node for this session (§9.5), keyed by <see cref="NodeViewModel.Key"/>.</summary>
    public Dictionary<string, ParameterValues> SessionValues { get; } = [];
    public DetailsViewModel Details { get; }
    public MyScriptsViewModel MyScripts { get; }
    public CommandPaletteViewModel Palette { get; }
    public OutputPanelViewModel Output { get; }
    public ObservableCollection<WorkspaceChoice> RecentWorkspaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkspaceSettingsCommand), nameof(OpenSchedulesCommand))]
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

    /// <summary>A page replaces the details panel while it is open.</summary>
    public bool IsPageOpen => WorkspaceSettings is not null || NewItem is not null || NewWorkspace is not null || Schedules is not null;

    private bool IsEditingPage => WorkspaceSettings is not null || NewItem is not null || NewWorkspace is not null || Schedules?.Editor is not null;

    [ObservableProperty]
    private bool isReloadPromptVisible;

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
        var loaded = WorkspaceLoader.Load(workspaceFile, _paths);
        var previous = Tree is not null && string.Equals(Workspace?.FilePath, loaded.FilePath, StringComparison.OrdinalIgnoreCase) ? Tree : null;
        if (Tree is not null)
            Tree.PropertyChanged -= OnTreePropertyChanged;

        Workspace = loaded;
        WorkspaceSettings = null;
        NewItem = null;
        NewWorkspace = null;
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
        Schedules?.Refresh();
        Tree.RefreshLinks(Time);
        if (previous is not null)
            CarryRunState(previous, Tree);
        RecordSeenBaseline(loaded);
        AdoptRuns(loaded);
        IsReloadPromptVisible = false;
        _fingerprint = Fingerprint();
        Watch();
        ScanProposals();

        if (File.Exists(loaded.FilePath))
        {
            _settings.AddRecentWorkspace(loaded.FilePath);
            _settings.Save(_paths.SettingsFile);
        }
        RefreshRecents();
    }

    /// <summary>Re-reads the workspace after a save and selects the first node matching <paramref name="select"/>.</summary>
    public void Reload(Func<NodeViewModel, bool>? select = null)
    {
        if (Workspace is null)
            return;
        Open(Workspace.FilePath);
        if (select is not null && Tree!.AllNodes.FirstOrDefault(select) is { } node)
        {
            for (var parent = node.Parent; parent is not null; parent = parent.Parent)
                parent.IsExpanded = true;
            node.IsSelected = true;
        }
    }

    /// <summary>Checks whether the config files or script folders changed since the last load; called by the watcher.</summary>
    public void CheckForExternalChanges()
    {
        if (Workspace is null)
            return;
        ApplyRenames(Workspace);
        if (Fingerprint() is not { } now)
            return;
        if (now == _fingerprint)
        {
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

    private void ApplyRenames(LoadedWorkspace workspace)
    {
        try
        {
            Renames.Apply(workspace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatchPad.Core.Config.ConfigException)
        {
        }
    }

    /// <summary>The latest proposal scan, which runs off the UI thread.</summary>
    public Task ProposalScan { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Refreshes the tree's proposal badges. Python and PowerShell probes run only for files changed since the last scan,
    /// so opening a workspace starts no process.
    /// </summary>
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatchPad.Core.Config.ConfigException)
        {
            return [];
        }
    }

    public void DismissProposal(NodeViewModel node, string proposalKey)
    {
        if (Workspace is null || node.ProposalKey is not { } scriptKey)
            return;
        try
        {
            UserStore.For(Workspace).DismissProposal(scriptKey, proposalKey);
            _fingerprint = Fingerprint();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatchPad.Core.Config.ConfigException)
        {
        }
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
            +$"Yes stops {them}. No leaves {them} running, and long-running ones come back when BatchPad opens again. "
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

    /// <summary>Records a long-running run in <c>running.json</c>, so it is adopted if BatchPad closes while it runs.</summary>
    public void TrackLongRunning(IRunProcess process, RunRequest request, NodeViewModel? node)
    {
        if (request.Script.LongRunning != true || node is null || Workspace is null || process.ProcessId is not { } processId)
            return;
        var secrets = SecretMasker.SecretNames(request);
        try
        {
            var entry = _running.Add(new RunningEntry
            {
                WorkspaceFile = Workspace.FilePath,
                NodeKey = node.Key,
                Name = node.Name,
                ProcessId = processId,
                Values = (request.Values ?? new Dictionary<string, JsonNode?>())
                    .Where(v => !secrets.Contains(v.Key))
                    .ToDictionary(v => v.Key, v => v.Value?.DeepClone()),
                ExtraArguments = request.ExtraArguments,
            });
            if (entry is not null)
                _ = process.Completion.ContinueWith(_ => ForgetRun(entry), TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void ForgetRun(RunningEntry entry)
    {
        try
        {
            _running.Remove(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void AdoptRuns(LoadedWorkspace loaded)
    {
        IReadOnlyList<AdoptedRun> adopted;
        try
        {
            adopted = _running.Adopt(loaded.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        foreach (var run in adopted)
        {
            var node = Tree!.AllNodes.FirstOrDefault(n => n.Key == run.Entry.NodeKey);
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
        _settings.Window = layout;
        _settings.Save(_paths.SettingsFile);
    }

    [RelayCommand]
    private void ReloadFromDisk()
    {
        WorkspaceSettings = null;
        NewItem = null;
        Details.LeaveEditMode();
        ReloadKeepingSelection();
    }

    [RelayCommand]
    private void DismissReloadPrompt() => IsReloadPromptVisible = false;

    [RelayCommand]
    private void Escape()
    {
        if (Palette.IsOpen)
            Palette.IsOpen = false;
        else if (Schedules is { Editor: not null } page)
            page.Editor = null;
        else if (IsPageOpen)
        {
            WorkspaceSettings = null;
            NewItem = null;
            NewWorkspace = null;
            Schedules = null;
        }
        else
        {
            Details.LeaveEditMode();
        }
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
        if (Tree?.AllNodes.FirstOrDefault(n => n.Key == nodeKey) is not { } node)
            return;
        SessionValues[nodeKey] = new ParameterValues(values, extraArguments);
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
            parent.IsExpanded = true;
        node.IsSelected = true;
        Details.RunWithSessionValues(node);
    }

    private void CarryRunState(TreeViewModel previous, TreeViewModel current)
    {
        var byKey = new Dictionary<string, NodeViewModel>();
        foreach (var node in current.AllNodes)
            byKey.TryAdd(node.Key, node);
        var previousNodes = previous.AllNodes.ToHashSet();
        foreach (var old in previousNodes.Where(n => n.Badge != RunBadge.None))
            if (byKey.TryGetValue(old.Key, out var match))
                match.AdoptRunState(old);
        foreach (var tab in Output.Tabs)
            if (tab.Node is { } old && previousNodes.Contains(old) && byKey.TryGetValue(old.Key, out var match))
                tab.Node = match;
    }

    /// <summary>Without <c>seenPaths</c> nothing shows as New, so the first open records every current script as seen (§3.10).</summary>
    private void RecordSeenBaseline(LoadedWorkspace loaded)
    {
        try
        {
            var store = UserStore.For(loaded);
            if (store.Load().SeenPaths is null)
                store.MarkSeen(ScriptFolderScanner.Scan(loaded.Workspace.BaseDirectory, loaded.Workspace.ScriptFolders).Select(s => s.RelativePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatchPad.Core.Config.ConfigException)
        {
        }
    }

    private void MarkSeen(NodeViewModel node)
    {
        node.IsNew = false;
        if (Workspace is null || node.Item?.ScriptPath is not { } path)
            return;
        try
        {
            UserStore.For(Workspace).MarkSeen([path]);
            _fingerprint = Fingerprint();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BatchPad.Core.Config.ConfigException)
        {
        }
    }

    /// <summary>The config files' contents plus every discovered script; null while a file is being written.</summary>
    private string? Fingerprint()
    {
        if (Workspace is null)
            return null;
        try
        {
            var trees = Workspace.AllTrees.ToList();
            var hashes = trees.Select(t => File.Exists(t.FilePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(t.FilePath))) : "-");
            var scripts = trees.SelectMany(t => ScriptFolderScanner.Scan(t.BaseDirectory, t.ScriptFolders)).Select(s => s.FullPath).Order(StringComparer.OrdinalIgnoreCase);
            return string.Join('\n', hashes.Concat(scripts));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!_watchFiles || Workspace is null)
            return;
        var trees = Workspace.AllTrees.ToList();
        _watcher = new FolderWatcher(trees.Select(t => t.BaseDirectory).Concat(trees.SelectMany(t => t.ScriptFolderDirectories)), TimeSpan.FromMilliseconds(500));
        _watcher.FileChanged += (_, change) => Renames.Observe(change);
        _watcher.FolderChanged += (_, _) => Services.Dispatcher.Post(CheckForExternalChanges);
    }

    private bool HasWorkspace() => Workspace is not null;

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private void OpenWorkspaceSettings()
    {
        NewItem = null;
        NewWorkspace = null;
        Schedules = null;
        var settings = new WorkspaceSettingsViewModel(this);
        settings.Closed += () => WorkspaceSettings = null;
        WorkspaceSettings = settings;
    }

    [RelayCommand]
    private void OpenNewWorkspace()
    {
        WorkspaceSettings = null;
        NewItem = null;
        Schedules = null;
        var wizard = new NewWorkspaceWizardViewModel(this);
        wizard.Closed += () => NewWorkspace = null;
        NewWorkspace = wizard;
    }

    private void OpenNewItem(NodeViewModel near, NewItemKind kind)
    {
        WorkspaceSettings = null;
        NewWorkspace = null;
        Schedules = null;
        if (kind == NewItemKind.Workflow)
        {
            NewItem = null;
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
        WorkspaceSettings = null;
        NewItem = null;
        NewWorkspace = null;
        if (Schedules is not null)
            return;
        var page = new SchedulesViewModel(this);
        page.Closed += () => Schedules = null;
        Schedules = page;
    }

    partial void OnSchedulesChanged(SchedulesViewModel? oldValue, SchedulesViewModel? newValue) => oldValue?.Detach();

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
            ScheduleFailed?.Invoke(failure);
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
        var node = Tree?.AllNodes.FirstOrDefault(n => n.Key == fire.Entry.Target?.NodeKey);
        var title = fire.Entry.Target?.Name ?? fire.Entry.Schedule.Target;
        OutputTabViewModel? tab = fire.Run switch
        {
            IRunProcess process => new RunViewModel(title, node, process, Services.Dispatcher),
            WorkflowRunOutput workflow => new WorkflowRunViewModel(title, node, workflow.Run, step => step.Id, Services.Dispatcher,
                new StepActions(Services.Workflows.StopStepAsync, Services.Opener, Sources)),
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
