using BatchPad.App.Services;
using BatchPad.Core.Config;
using BatchPad.Core.Detection;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels.Editor;

/// <summary>Edits a copy of a script's definition (§5.1); nothing is written until <see cref="SaveCommand"/>.</summary>
public sealed partial class ScriptEditorViewModel : ObservableObject, IUnsavedEdits
{
    private readonly MainViewModel _main;
    private NodeViewModel _node;
    private readonly ScriptNode _original;
    private (string Path, string Id)? _newCompanion;
    private readonly string _initial;

    public ScriptEditorViewModel(MainViewModel main, NodeViewModel node)
    {
        _main = main;
        _node = node;
        _original = node.Script!;
        Definition = ConfigJson.Clone(_original);
        General = new GeneralTabViewModel(Definition, node.Name, Refresh, HotkeyWarning, SuspendHotkeys);
        var proposals = DetectProposals();
        Parameters = new ParametersTabViewModel(Definition, main.Workspace!.Workspace.File.SharedParams?.Keys ?? Enumerable.Empty<string>(),
            proposals.Parameters, Refresh, key => main.DismissProposal(node, key));
        Environment = new EnvironmentTabViewModel(Definition, node.Tree.BaseDirectory, main.Services.Dialogs, Refresh);
        AfterRun = new AfterRunTabViewModel(Definition, StopChoices(main, node), Refresh,
            () => RunPlanner.BoundTemplatesFor(Request()), () => main.History.LastLog(node.Key));
        Advanced = new AdvancedTabViewModel(Definition, Refresh);
        if (proposals.LongRunningReason is { } reason)
            Parameters.Proposals.Add(new ProposalViewModel(ProposalTracker.LongRunningKey, $"long-running · {reason}",
                "Keeps running until stopped", Parameters) { Apply = () => General.LongRunning = true });
        if (proposals.StopCompanion is { } companion)
            Parameters.Proposals.Add(new ProposalViewModel(ProposalTracker.StopKey(companion), $"stop with {companion}",
                "Runs it with the same values to stop this script", Parameters) { Apply = () => AcceptStop(companion) });
        ChoiceEnvironment = new ChoiceEnvironment(ChoiceEnvironment.ContextFor(main.Workspace, node.Tree, Definition, main.CommandChoices),
            main.Services.Dialogs, main.Services.Dispatcher);
        Parameters.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ParametersTabViewModel.Selected))
                Choices = BuildChoices();
        };
        choices = BuildChoices();
        Refresh();
        _initial = ConfigJson.Serialize(Definition);
    }

    public bool HasUnsavedEdits => _newCompanion is not null || ConfigJson.Serialize(Definition) != _initial;

    public string EditsDescription => $"'{_node.Name}'";

    public ChoiceEnvironment ChoiceEnvironment { get; }

    /// <summary>The Choices tab for the selected parameter; null for a <c>use</c> entry, whose choices live in the shared definition.</summary>
    [ObservableProperty]
    private ChoicesTabViewModel? choices;

    private ChoicesTabViewModel? BuildChoices() =>
        Parameters.Selected is { IsShared: false } selected ? new ChoicesTabViewModel(selected.Definition, ChoiceEnvironment, Refresh) : null;

    public ScriptNode Definition { get; }
    public GeneralTabViewModel General { get; }
    public ParametersTabViewModel Parameters { get; }
    public EnvironmentTabViewModel Environment { get; }
    public AfterRunTabViewModel AfterRun { get; }
    public AdvancedTabViewModel Advanced { get; }
    public string Title => _node.Name;

    public bool IsShared => _node.Tree.Kind != TreeKind.MyScripts;

    public string SharedBannerText => _node.Tree.Kind == TreeKind.Workspace
        ? $"Shared script — saved to {Path.GetFileName(_node.Tree.FilePath)} in the repository. Everyone who pulls gets this change."
        : "Shared script — saved to the global library. Every workspace on this computer gets this change.";

    [ObservableProperty]
    private string preview = "";

    [ObservableProperty]
    private string? error;

    /// <summary>Raised when the editor should close; true after a save.</summary>
    public event Action<bool>? Closed;

    public NodeViewModel Node => _node;

    /// <summary>Whether the entry this edit started from is unchanged on disk, so the edit survives a reload.</summary>
    public bool PendingEditStillApplies()
    {
        try
        {
            var current = File.Exists(_node.Tree.FilePath) ? ConfigReader.ReadFile(_node.Tree.FilePath) : new WorkspaceFile();
            return EntryMerge.StillApplies(current, _original, _node.Item?.HasEntry == true);
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            return false;
        }
    }

    /// <summary>Moves the open edit onto the reloaded tree's node; saving still matches the entry it started from.</summary>
    public void Rebind(NodeViewModel node) => _node = node;

    public RunRequest Request() => new(_main.Workspace!, _node.Tree, Definition);

    public void Refresh()
    {
        try
        {
            Preview = string.Join(System.Environment.NewLine,
                RunPlanner.Plan(Request(), _main.Services.Interpreters).Select(s => s.Command.DisplayRelativeTo(_main.Workspace!.Directory)));
        }
        catch (Exception ex) when (RunProblems.IsRunProblem(ex))
        {
            Preview = $"⚠ {ex.Message}";
        }
        Advanced.Preview = Preview;
        AfterRun.UpdateRerunParams(Definition.Params);
        TestRunCommand.NotifyCanExecuteChanged();
    }

    private bool CanTestRun() => _main.IsTrusted;

    [RelayCommand(CanExecute = nameof(CanTestRun))]
    private void TestRun() =>
        _main.Details.Launch(new RunRequest(_main.Workspace!, _node.Tree, ConfigJson.Clone(Definition)), _node);

    [RelayCommand]
    private void Cancel() => Closed?.Invoke(false);

    [RelayCommand]
    private void Save()
    {
        var tree = _node.Tree;
        var indexPath = _node.Item?.HasEntry == true ? ConfigEntries.IndexPath(tree.File.Scripts, _original) : null;
        ScriptNode saved = null!;
        try
        {
            ConfigWriter.Update(tree.FilePath, file =>
            {
                if (_newCompanion is { } companion)
                    AddCompanionEntry(file.Scripts, companion.Path, companion.Id);
                saved = ConfigJson.Clone(Definition);
                if (!ConfigEntries.Replace(file.Scripts, indexPath, _original, saved))
                {
                    saved.Id ??= IdAssigner.FromFileName(saved.Path ?? saved.Name ?? "script", ConfigEntries.Ids(file.Scripts, _main.Workspace!, tree));
                    file.Scripts.Add(saved);
                }
            });
        }
        catch (Exception ex) when (IoProblems.IsIoProblem(ex))
        {
            Error = ex.Message;
            return;
        }
        Closed?.Invoke(true);
        _main.Reload(n => n.Tree.Kind == tree.Kind && n.Script is { } s
            && (saved.Id is not null ? s.Id == saved.Id : s.Path == saved.Path));
    }

    private void SuspendHotkeys(bool suspend)
    {
        if (suspend)
            _main.Hotkeys?.Suspend();
        else
            _main.Hotkeys?.Resume();
    }

    private string? HotkeyWarning(HotkeyGesture gesture) =>
        _main.Tree!.AllNodes.FirstOrDefault(n => n.Key != _node.Key && n.Hotkey == gesture) is { } owner
            ? HotkeyMessages.Duplicate(gesture, owner.Name)
            : _main.Hotkeys?.IsTaken(gesture) == true ? HotkeyMessages.Taken(gesture) : null;

    /// <summary>Scripts with an id that could stop this one, as references it can resolve.</summary>
    private static IReadOnlyList<EditorOption<string?>> StopChoices(MainViewModel main, NodeViewModel self) =>
    [
        new(null, "None — close its windows, then end it"),
        .. main.Tree!.AllNodes
            .Where(n => n != self && n.Node is ScriptNode { Id: not null } && (n.Tree.Kind == self.Tree.Kind || n.Tree.Kind != TreeKind.MyScripts))
            .Select(n => (Node: n, Id: ((ScriptNode)n.Node!).Id!))
            .Select(n => new EditorOption<string?>(
                n.Node.Tree.Scope == self.Tree.Scope ? n.Id : ReferenceResolver.Qualified(n.Node.Tree, n.Id),
                $"{n.Node.Location} › {n.Node.Name}")),
    ];

    /// <summary>Which editor tab is shown; the tree's proposal badge opens Parameters.</summary>
    [ObservableProperty]
    private int selectedTab;

    public const int ParametersTabIndex = 1;

    private ScriptProposals DetectProposals()
    {
        if (_node.ScriptFullPath is not { } fullPath)
            return ScriptProposals.None;
        try
        {
            return ProposalTracker.For(_original, fullPath, _main.DismissedProposals(_node), _main.Probes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ScriptProposals.None;
        }
    }

    /// <summary>A stop reference needs an id, so a companion without an entry gets one when this script is saved.</summary>
    private void AcceptStop(string companion)
    {
        var fullPath = Path.Combine(Path.GetDirectoryName(_node.ScriptFullPath!)!, companion);
        var existing = _main.Tree!.AllNodes.FirstOrDefault(n =>
            n.Tree == _node.Tree && string.Equals(n.ScriptFullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing?.Script?.Id is not { } id)
        {
            id = IdAssigner.FromFileName(companion, ConfigEntries.Ids(_node.Tree.File.Scripts));
            _newCompanion = (Path.GetRelativePath(_node.Tree.BaseDirectory, fullPath).Replace('\\', '/'), id);
        }
        AfterRun.SelectStop(id, existing?.Name ?? companion);
    }

    private static void AddCompanionEntry(List<TreeNode> scripts, string path, string id)
    {
        if (FindEntry(scripts, path) is { } entry)
            entry.Id ??= id;
        else
            scripts.Add(new ScriptNode { Path = path, Id = id });
    }

    private static ScriptNode? FindEntry(IEnumerable<TreeNode> nodes, string path) =>
        nodes.Select(n => n switch
        {
            ScriptNode s when string.Equals(s.Path?.Replace('\\', '/'), path, StringComparison.OrdinalIgnoreCase) => s,
            FolderNode f => FindEntry(f.Items, path),
            _ => null,
        }).FirstOrDefault(s => s is not null);
}
