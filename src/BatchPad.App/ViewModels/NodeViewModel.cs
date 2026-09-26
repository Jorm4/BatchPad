using System.Collections.ObjectModel;
using BatchPad.Core.Customisation;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BatchPad.App.ViewModels;

public enum NodeKind { Root, Folder, Script, Workflow, Link }

public enum RunBadge { None, Running, Ready, Passed, Failed }

public sealed partial class NodeViewModel : ObservableObject
{
    private readonly Action<NodeViewModel>? _onSelected;
    private int _activeRuns;
    private RunBadge _lastResult;

    public NodeViewModel(string name, NodeKind kind, ScriptTree tree, NodeViewModel? parent, TreeItem? item,
        Action<NodeViewModel>? onSelected, ResolvedCustomisation? customisation = null)
    {
        Customisation = customisation;
        Name = name;
        Kind = kind;
        Tree = tree;
        Parent = parent;
        Item = item;
        _onSelected = onSelected;
        isExpanded = kind == NodeKind.Root || item?.Node is FolderNode { Collapsed: false };
        isNew = item?.IsNew == true;
    }

    public string Name { get; }
    public string Header => Kind == NodeKind.Root ? Name.ToUpperInvariant() : Name;
    public NodeKind Kind { get; }
    public ScriptTree Tree { get; }
    public NodeViewModel? Parent { get; }
    public TreeItem? Item { get; }
    public ObservableCollection<NodeViewModel> Children { get; } = [];

    public TreeNode? Node => Item?.Node;
    public ScriptNode? Script => Item?.Node as ScriptNode;
    public bool IsOrphan => Item?.IsOrphan == true;
    public bool IsRunnable => Kind is NodeKind.Script or NodeKind.Workflow;
    public bool IsMyScript => Tree.Kind == TreeKind.MyScripts && Item?.HasEntry == true;

    /// <summary>A My Scripts script entry with its base applied; null elsewhere.</summary>
    public ResolvedCustomisation? Customisation { get; }
    public bool IsBroken => Customisation?.IsBroken == true;

    public string? Description => Item?.Node switch
    {
        ScriptNode when Customisation is { IsBroken: true } broken => broken.Problem,
        ScriptNode when Customisation?.Definition is { } definition => definition.Description,
        RunnableNode r => r.Description,
        LinkNode l => l.Description ?? l.Url,
        _ => null,
    };

    public string? FilePath => Item?.ScriptPath ?? Script?.Path;

    /// <summary>Folders from the root down to this node's parent, e.g. <c>Workspace › Hello</c>.</summary>
    public string Location => string.Join(" › ", Ancestors().Reverse().Select(a => a.Kind == NodeKind.Root ? RootLabel(a.Tree.Kind) : a.Name));

    public string AutomationId => Parent is null ? Tree.Kind.ToString() : $"{Parent.AutomationId}/{Name}";

    /// <summary>Identifies the node across reloads and renames: its tree plus its id, else its file.</summary>
    public string Key => $"{Tree.Kind}:" + (Node is { } node && ReferenceResolver.IdOf(node) is { } id ? "id:" + id
        : FilePath is { } path ? "path:" + Path.GetFullPath(path, Tree.BaseDirectory).ToLowerInvariant() : AutomationId);

    public string Icon => Kind switch
    {
        NodeKind.Root => Tree.Kind switch
        {
            TreeKind.MyScripts => "",
            TreeKind.Workspace => "",
            _ => "",
        },
        NodeKind.Folder => "",
        NodeKind.Workflow => "",
        NodeKind.Link => "",
        _ => "",
    };

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isSelected;

    [ObservableProperty]
    private bool isVisible = true;

    [ObservableProperty]
    private bool isNew;

    [ObservableProperty]
    private bool isRenaming;

    [ObservableProperty]
    private string renameText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    private RunBadge badge;

    public bool IsRunning => _activeRuns > 0;

    public void OnRunStarted()
    {
        _activeRuns++;
        Badge = RunBadge.Running;
    }

    public void OnRunReady()
    {
        if (_activeRuns > 0)
            Badge = RunBadge.Ready;
    }

    /// <summary>A stop keeps the previous pass/fail, since stopping a server is not a failure.</summary>
    public void OnRunFinished(RunResult result)
    {
        _activeRuns--;
        if (result.Outcome != RunOutcome.Stopped)
            _lastResult = result.Succeeded ? RunBadge.Passed : RunBadge.Failed;
        Badge = _activeRuns > 0 ? RunBadge.Running : _lastResult;
    }

    public void AdoptRunState(NodeViewModel previous)
    {
        _activeRuns = previous._activeRuns;
        _lastResult = previous._lastResult;
        Badge = previous.Badge;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            _onSelected?.Invoke(this);
    }

    public bool IsWithin(NodeViewModel ancestor) => this == ancestor || Ancestors().Contains(ancestor);

    public IEnumerable<NodeViewModel> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));

    public static string RootLabel(TreeKind kind) => kind switch
    {
        TreeKind.MyScripts => "My Scripts",
        TreeKind.Workspace => "Workspace",
        _ => "Global",
    };

    private IEnumerable<NodeViewModel> Ancestors()
    {
        for (var node = Parent; node is not null; node = node.Parent)
            yield return node;
    }
}
