using System.Collections.ObjectModel;
using BatchPad.Core.Customisation;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public enum NewItemKind { Link, Folder, Workflow, Entry, Script }

public sealed partial class TreeViewModel : ObservableObject
{
    private readonly Action<NodeViewModel, NewItemKind>? _onNewItem;
    private readonly CustomisationResolver _customisations;
    private readonly Lazy<Dictionary<string, NodeViewModel>> _byKey;
    private readonly Lazy<Dictionary<TreeNode, NodeViewModel>> _byDefinition;

    /// <param name="onNewItem">Called with the node the new item goes into or next to.</param>
    public TreeViewModel(LoadedWorkspace workspace, Action<NodeViewModel, NewItemKind>? onNewItem = null)
    {
        _onNewItem = onNewItem;
        _customisations = new CustomisationResolver(workspace);
        var workspaceName = workspace.Workspace.File.Name ?? Path.GetFileName(workspace.Directory);
        Roots =
        [
            BuildRoot(workspace.MyScripts, "My Scripts"),
            BuildRoot(workspace.Workspace, $"Workspace — {workspaceName}"),
            BuildRoot(workspace.Global, "Global"),
        ];
        _byKey = new(() => Index(n => n.Key, StringComparer.Ordinal));
        _byDefinition = new(() => Index<TreeNode>(n => n.Node, ReferenceEqualityComparer.Instance));
    }

    public ObservableCollection<NodeViewModel> Roots { get; }

    public NodeViewModel MyScriptsRoot => Roots[0];

    public IEnumerable<NodeViewModel> AllNodes => Roots.SelectMany(r => r.Descendants().Prepend(r));

    [ObservableProperty]
    private NodeViewModel? selectedNode;

    [ObservableProperty]
    private string filter = "";

    partial void OnFilterChanged(string value)
    {
        var text = value.Trim();
        foreach (var root in Roots)
        {
            root.IsVisible = true;
            foreach (var child in root.Children)
                ApplyFilter(child, text);
        }
    }

    [RelayCommand]
    private void NewLink(NodeViewModel? near) => _onNewItem?.Invoke(near ?? Roots[1], NewItemKind.Link);

    [RelayCommand]
    private void NewWorkflow(NodeViewModel? near) => _onNewItem?.Invoke(near ?? Roots[1], NewItemKind.Workflow);

    [RelayCommand]
    private void NewEntry(NodeViewModel? near) => _onNewItem?.Invoke(near ?? Roots[1], NewItemKind.Entry);

    [RelayCommand]
    private void NewScript(NodeViewModel? near) => _onNewItem?.Invoke(near ?? Roots[1], NewItemKind.Script);

    [RelayCommand]
    private void NewFolder(NodeViewModel? near) => _onNewItem?.Invoke(near ?? Roots[1], NewItemKind.Folder);

    public void RefreshLinks(TimeProvider time)
    {
        foreach (var node in AllNodes.Where(n => n.Kind == NodeKind.Link))
            node.RefreshLinkState(time);
    }

    public NodeViewModel? Find(string automationId) => AllNodes.FirstOrDefault(n => n.AutomationId == automationId);

    public NodeViewModel? ByKey(string? key) => key is null ? null : _byKey.Value.GetValueOrDefault(key);

    public NodeViewModel? ByDefinition(TreeNode? definition) => definition is null ? null : _byDefinition.Value.GetValueOrDefault(definition);

    private Dictionary<TKey, NodeViewModel> Index<TKey>(Func<NodeViewModel, TKey?> keyOf, IEqualityComparer<TKey> comparer) where TKey : notnull
    {
        var index = new Dictionary<TKey, NodeViewModel>(comparer);
        foreach (var node in AllNodes)
            if (keyOf(node) is { } key)
                index.TryAdd(key, node);
        return index;
    }

    private static bool ApplyFilter(NodeViewModel node, string text)
    {
        var anyChildVisible = false;
        foreach (var child in node.Children)
            anyChildVisible |= ApplyFilter(child, text);

        node.IsVisible = text.Length == 0 || anyChildVisible || node.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
            || node.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;
        if (text.Length > 0 && anyChildVisible)
            node.IsExpanded = true;
        return node.IsVisible;
    }

    private NodeViewModel BuildRoot(ScriptTree tree, string name)
    {
        var root = new NodeViewModel(name, NodeKind.Root, tree, null, null, Select);
        AddChildren(root, tree, tree.Items);
        return root;
    }

    private void AddChildren(NodeViewModel parent, ScriptTree tree, IEnumerable<TreeItem> items)
    {
        foreach (var item in items)
        {
            var customisation = tree.Kind == TreeKind.MyScripts && item.Node is ScriptNode entry ? _customisations.Resolve(entry) : null;
            var itemTree = item.Part ?? tree;
            var node = new NodeViewModel(customisation?.Name ?? item.Name,
                customisation?.Definition is WorkflowNode ? NodeKind.Workflow : KindOf(item.Node), itemTree, parent, item, Select, customisation);
            AddChildren(node, itemTree, item.Children);
            parent.Children.Add(node);
        }
    }

    private void Select(NodeViewModel node)
    {
        if (SelectedNode is { } previous && previous != node)
            previous.IsSelected = false;
        SelectedNode = node;
    }

    private static NodeKind KindOf(TreeNode node) => node switch
    {
        FolderNode => NodeKind.Folder,
        WorkflowNode => NodeKind.Workflow,
        LinkNode => NodeKind.Link,
        _ => NodeKind.Script,
    };
}
