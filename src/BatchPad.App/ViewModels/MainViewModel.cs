using System.Collections.ObjectModel;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.App.ViewModels.Wizard;
using BatchPad.App.ViewModels.Workflows;
using BatchPad.App.ViewModels.Workspace;
using System.Security.Cryptography;
using BatchPad.Core.Discovery;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed record WorkspaceChoice(string FilePath)
{
    public string DisplayName => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? FilePath;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppPaths _paths;
    private readonly Settings _settings;
    private bool _opening;
    private bool _watchFiles;
    private FolderWatcher? _watcher;
    private string? _fingerprint;

    /// <param name="launcher">Null starts runs through the real <see cref="RunGate"/>.</param>
    /// <param name="dispatcher">Null runs output callbacks inline, which suits tests only.</param>
    public MainViewModel(AppPaths paths, Settings settings,
        IRunLauncher? launcher = null, IUiDispatcher? dispatcher = null, IShellService? shell = null, IFileDialogService? dialogs = null,
        IConfirmService? confirm = null, IWorkflowLauncher? workflows = null)
    {
        _paths = paths;
        _settings = settings;
        Trust = new TrustStore(settings, paths.SettingsFile);
        var interpreters = new InterpreterLocator(settings.Interpreters);
        var gate = new RunGate(Trust);
        shell ??= new ShellService();
        Services = new AppServices(launcher ?? new GatedRunLauncher(gate, interpreters), interpreters,
            dispatcher ?? new ImmediateDispatcher(), shell, dialogs ?? new FileDialogService(),
            confirm ?? new MessageBoxConfirmService(), workflows ?? new GatedWorkflowLauncher(gate, interpreters, new ShellOpener(shell)));
        Details = new DetailsViewModel(this);
        MyScripts = new MyScriptsViewModel(this);
        RefreshRecents();
    }

    public TrustStore Trust { get; }
    public AppServices Services { get; }

    /// <summary>Form values per node for this session (§9.5), keyed by <see cref="NodeViewModel.Key"/>.</summary>
    public Dictionary<string, ParameterValues> SessionValues { get; } = [];
    public DetailsViewModel Details { get; }
    public MyScriptsViewModel MyScripts { get; }
    public OutputPanelViewModel Output { get; } = new();
    public ObservableCollection<WorkspaceChoice> RecentWorkspaces { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenWorkspaceSettingsCommand))]
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

    /// <summary>A page replaces the details panel while it is open.</summary>
    public bool IsPageOpen => WorkspaceSettings is not null || NewItem is not null || NewWorkspace is not null;

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
        if (previous is not null)
            CarryRunState(previous, Tree);
        RecordSeenBaseline(loaded);
        IsReloadPromptVisible = false;
        _fingerprint = Fingerprint();
        Watch();

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
        if (Workspace is null || Fingerprint() is not { } now || now == _fingerprint)
            return;
        _fingerprint = now;
        if (IsPageOpen || Details.IsEditing)
            IsReloadPromptVisible = true;
        else
            ReloadKeepingSelection();
    }

    public void EnableFileWatching()
    {
        _watchFiles = true;
        Watch();
    }

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
        if (IsPageOpen)
        {
            WorkspaceSettings = null;
            NewItem = null;
            NewWorkspace = null;
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
            var trees = new[] { Workspace.MyScripts, Workspace.Workspace, Workspace.Global };
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
        var trees = new[] { Workspace.MyScripts, Workspace.Workspace, Workspace.Global };
        _watcher = new FolderWatcher(trees.Select(t => t.BaseDirectory).Concat(trees.SelectMany(t => t.ScriptFolderDirectories)), TimeSpan.FromMilliseconds(500));
        _watcher.FolderChanged += (_, _) => Services.Dispatcher.Post(CheckForExternalChanges);
    }

    private bool HasWorkspace() => Workspace is not null;

    [RelayCommand(CanExecute = nameof(HasWorkspace))]
    private void OpenWorkspaceSettings()
    {
        NewItem = null;
        NewWorkspace = null;
        var settings = new WorkspaceSettingsViewModel(this);
        settings.Closed += () => WorkspaceSettings = null;
        WorkspaceSettings = settings;
    }

    [RelayCommand]
    private void OpenNewWorkspace()
    {
        WorkspaceSettings = null;
        NewItem = null;
        var wizard = new NewWorkspaceWizardViewModel(this);
        wizard.Closed += () => NewWorkspace = null;
        NewWorkspace = wizard;
    }

    private void OpenNewItem(NodeViewModel near, NewItemKind kind)
    {
        WorkspaceSettings = null;
        NewWorkspace = null;
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
