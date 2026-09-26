using System.Text.Json;
using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Parameters;
using BatchPad.Core.Config;
using BatchPad.Core.Customisation;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public sealed record BaseChoice(string Label, string Reference)
{
    public override string ToString() => Label;
}

/// <summary>Edits the My Scripts tree (§3.7, §5): every change goes through <see cref="UserStore"/>, then the workspace reloads.</summary>
public sealed partial class MyScriptsViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public MyScriptsViewModel(MainViewModel main)
    {
        _main = main;
        DragDrop = new DragDropHandler(main, this);
    }

    public DragDropHandler DragDrop { get; }

    [ObservableProperty]
    private string? error;

    public static bool CanAdd(NodeViewModel? source) =>
        source is { IsRunnable: true, Tree.Kind: not TreeKind.MyScripts }
        && source.Node is RunnableNode { Id: not null } or ScriptNode { Path: not null };

    private static bool IsEntry(NodeViewModel? node) => node is { IsMyScript: true };

    private static bool IsRunnableEntry(NodeViewModel? node) => node is { IsMyScript: true, Node: RunnableNode };

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void AddToMyScripts(NodeViewModel? source) => Add(source!, _main.Tree!.MyScriptsRoot);

    public static bool CanShare(NodeViewModel? node) => node is { IsMyScript: true, Node: RunnableNode, Customisation.IsBroken: false };

    public static bool CanCopy(NodeViewModel? node) => node is { Tree.Kind: not TreeKind.MyScripts, Kind: NodeKind.Script, Node: ScriptNode };

    [RelayCommand(CanExecute = nameof(CanShare))]
    private void ShareWithWorkspace(NodeViewModel? node) => Share(node!);

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void CopyToMyScripts(NodeViewModel? node) =>
        AddNodes([Flattener.ToStandalone((ScriptNode)node!.Node!, node.Tree, node.Name)], _main.Tree!.MyScriptsRoot);

    [RelayCommand(CanExecute = nameof(IsRunnableEntry))]
    private void Duplicate(NodeViewModel? node) => DuplicateEntry(node!);

    [RelayCommand(CanExecute = nameof(IsEntry))]
    private void Delete(NodeViewModel? node) => DeleteEntry(node!);

    [RelayCommand(CanExecute = nameof(IsEntry))]
    private void BeginRename(NodeViewModel? node)
    {
        node!.RenameText = node.Name;
        node.IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename(NodeViewModel? node)
    {
        if (node is not null)
            node.IsRenaming = false;
    }

    [RelayCommand]
    private void CommitRename(NodeViewModel? node)
    {
        if (node is not { IsRenaming: true })
            return;
        node.IsRenaming = false;
        Rename(node, node.RenameText);
    }

    /// <summary>Adds a customisation of <paramref name="source"/> with its current form values, placed at <paramref name="target"/>.</summary>
    public bool Add(NodeViewModel source, NodeViewModel target)
    {
        if (!CanAdd(source) || _main.Workspace is null)
            return false;
        var entry = EntryFor(source);
        var (folderPath, index) = PlaceAt(target);
        return Edit(file =>
        {
            entry.Id = IdAssigner.FromName(entry.Name!, ConfigEntries.Ids(file.Scripts));
            Insert(ConfigEntries.FolderItems(file.Scripts, folderPath), index, entry);
        }, () => ById(entry.Id));
    }

    /// <summary>Moves a My Scripts entry into the workspace file as a real entry, after asking (§5.1).</summary>
    public bool Share(NodeViewModel node)
    {
        if (_main.Workspace is not { } workspace || node.Customisation is not { IsBroken: false } resolved
            || !_main.Services.Confirm.Confirm("Share with workspace",
                $"Move '{node.Name}' from My Scripts into {Path.GetFileName(workspace.FilePath)}? Everyone who pulls the repository gets it."))
            return false;
        Error = null;
        string? id = null;
        try
        {
            var store = UserStore.For(workspace);
            Locate(store.Load(), node);
            var target = workspace.Workspace;
            ConfigWriter.Update(target.FilePath, file =>
            {
                var shared = Flattener.ToShared(resolved, target, ConfigEntries.Ids(file.Scripts, workspace, target));
                id = shared.Id;
                file.Scripts.Add(shared);
            });
            store.Update(file =>
            {
                var (list, index) = Locate(file, node);
                list.RemoveAt(index);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException or StaleEntryException)
        {
            Error = ex.Message;
        }
        _main.Reload(id is null ? null : n => n.Tree.Kind == TreeKind.Workspace && n.Node is RunnableNode r && r.Id == id);
        return Error is null;
    }

    /// <summary>Adds new entries or links at <paramref name="target"/>, giving each an id.</summary>
    public bool AddNodes(IReadOnlyList<TreeNode> nodes, NodeViewModel target)
    {
        var (folderPath, index) = PlaceAt(target);
        return Edit(file =>
        {
            var ids = ConfigEntries.Ids(file.Scripts);
            var into = ConfigEntries.FolderItems(file.Scripts, folderPath);
            for (var i = 0; i < nodes.Count; i++)
            {
                DragDropHandler.SetId(nodes[i], ids);
                Insert(into, index is { } at ? at + i : null, nodes[i]);
            }
        }, () => ById(ReferenceResolver.IdOf(nodes[0])));
    }

    public bool PinLink(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        return _main.Tree is { } tree && AddNodes([new LinkNode { Name = Path.GetFileName(full), Url = full }], tree.MyScriptsRoot);
    }

    public bool Move(NodeViewModel dragged, NodeViewModel target)
    {
        var (folderPath, index) = PlaceAt(target);
        return Edit(file =>
        {
            var (from, fromIndex) = Locate(file, dragged);
            var to = ConfigEntries.FolderItems(file.Scripts, folderPath);
            var moved = from[fromIndex];
            from.RemoveAt(fromIndex);
            var at = index is { } i ? (ReferenceEquals(from, to) && fromIndex < i ? i - 1 : i) : to.Count;
            Insert(to, at, moved);
        }, () => Same(dragged));
    }

    public bool DuplicateEntry(NodeViewModel node)
    {
        string? id = null;
        var name = IsAutoNamed(node) ? node.Name : $"{node.Name} (copy)";
        return Edit(file =>
        {
            var (list, index) = Locate(file, node);
            var copy = (RunnableNode)ConfigEntries.Clone(list[index]);
            copy.Name = name;
            copy.Id = id = IdAssigner.FromName(name, ConfigEntries.Ids(file.Scripts));
            list.Insert(index + 1, copy);
        }, () => ById(id));
    }

    public bool DeleteEntry(NodeViewModel node)
    {
        var what = node.Kind == NodeKind.Folder && node.Children.Count > 0 ? $"the folder '{node.Name}' and everything in it" : $"'{node.Name}'";
        if (!_main.Services.Confirm.Confirm("Delete", $"Remove {what} from My Scripts?"))
            return false;
        return Edit(file =>
        {
            var (list, index) = Locate(file, node);
            list.RemoveAt(index);
        }, null);
    }

    public bool Rename(NodeViewModel node, string newName)
    {
        var name = newName.Trim();
        if (name.Length == 0 || name == node.Name)
            return false;
        return Edit(file =>
        {
            var (list, index) = Locate(file, node);
            switch (list[index])
            {
                case FolderNode folder:
                    folder.Folder = name;
                    break;
                case RunnableNode runnable:
                    runnable.Name = name;
                    break;
                case LinkNode link:
                    link.Name = name;
                    break;
            }
        }, () => ReferenceResolver.IdOf(node.Node!) is { } id ? ById(id) : n => n.IsMyScript && n.Kind == node.Kind && n.Name == name);
    }

    /// <summary>Saves the form's values on a My Scripts entry; an entry still named by its <c>nameTemplate</c> is renamed to match.</summary>
    public bool SaveValues(NodeViewModel node, ParameterValues values)
    {
        var stored = values.Values.Count == 0 ? null : values.Values.ToDictionary(v => v.Key, v => v.Value?.DeepClone());
        var newName = IsAutoNamed(node) && node.Customisation is { Definition: { } definition, DefinitionTree: { } tree }
            ? new CustomisationResolver(_main.Workspace!).NameFor(definition, tree, stored)
            : null;
        return Edit(file =>
        {
            var (list, index) = Locate(file, node);
            var entry = (ScriptNode)list[index];
            entry.Values = stored;
            entry.ExtraArgs = values.ExtraArguments.Trim().Length == 0 ? null : values.ExtraArguments.Trim();
            if (newName is not null)
                entry.Name = newName;
        }, () => Same(node));
    }

    public bool ChangeBase(NodeViewModel node, string reference) => Edit(file =>
    {
        var (list, index) = Locate(file, node);
        ((ScriptNode)list[index]).Base = reference;
    }, () => Same(node));

    public IReadOnlyList<BaseChoice> BaseChoices() =>
        _main.Tree?.AllNodes
            .Where(n => n.Tree.Kind != TreeKind.MyScripts && n.Node is RunnableNode { Id: not null })
            .Select(n => new BaseChoice($"{n.Location} › {n.Name}", Reference(n)))
            .ToList() ?? [];

    private ScriptNode EntryFor(NodeViewModel source)
    {
        var session = _main.SessionValues.GetValueOrDefault(source.Key);
        var values = session is { Values.Count: > 0 } ? session.Values.ToDictionary(v => v.Key, v => v.Value?.DeepClone()) : null;
        var extraArgs = session?.ExtraArguments.Trim() is { Length: > 0 } extra ? extra : null;
        var runnable = (RunnableNode)source.Node!;
        if (runnable.Id is not null)
        {
            return new ScriptNode
            {
                Base = Reference(source),
                Name = new CustomisationResolver(_main.Workspace!).NameFor(runnable, source.Tree, values),
                Values = values,
                ExtraArgs = extraArgs,
            };
        }
        var standalone = (ScriptNode)ConfigEntries.Clone((TreeNode)runnable);
        standalone.Path = Path.GetFullPath(Path.Combine(source.Tree.BaseDirectory, standalone.Path!));
        standalone.Name ??= source.Name;
        standalone.Values = values;
        standalone.ExtraArgs = extraArgs;
        return standalone;
    }

    private static string Reference(NodeViewModel node) =>
        ReferenceResolver.Qualified(node.Tree, ((RunnableNode)node.Node!).Id!);

    private bool IsAutoNamed(NodeViewModel node) =>
        node.Customisation is { Entry.Name: { } name, Definition: { NameTemplate: not null } definition, DefinitionTree: { } tree } resolved
        && name == new CustomisationResolver(_main.Workspace!).NameFor(definition, tree, resolved.Values);

    /// <summary>A folder or the root takes the item at its end; an entry takes it just before itself.</summary>
    private static (IReadOnlyList<int>? FolderPath, int? Index) PlaceAt(NodeViewModel target)
    {
        var scripts = target.Tree.File.Scripts;
        if (target.Kind == NodeKind.Root || target.Item is not { HasEntry: true, Node: var node })
            return (null, null);
        if (node is FolderNode)
            return (ConfigEntries.IndexPath(scripts, node), null);
        var path = ConfigEntries.IndexPath(scripts, node);
        return path is null ? (null, null) : (path.Take(path.Count - 1).ToList(), path[^1]);
    }

    private static void Insert(List<TreeNode> items, int? index, TreeNode node) =>
        items.Insert(Math.Min(index ?? items.Count, items.Count), node);

    /// <summary>The loaded node's entry in a freshly read file, found by its position and checked by content.</summary>
    private static (List<TreeNode> List, int Index) Locate(WorkspaceFile file, NodeViewModel node)
    {
        if (node.Node is { } entry && ConfigEntries.IndexPath(node.Tree.File.Scripts, entry) is { } path
            && ConfigEntries.At(file.Scripts, path) is ({ } list, var index) && Json(list[index]) == Json(entry))
            return (list, index);
        throw new StaleEntryException();
    }

    private static string Json(TreeNode node) => JsonSerializer.Serialize(node, ConfigJson.Options);

    private bool Edit(Action<WorkspaceFile> change, Func<Func<NodeViewModel, bool>>? select)
    {
        Error = null;
        var saved = true;
        try
        {
            UserStore.For(_main.Workspace!).Update(change);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException or StaleEntryException)
        {
            Error = ex.Message;
            saved = false;
        }
        _main.Reload(saved ? select?.Invoke() : null);
        return saved;
    }

    private static Func<NodeViewModel, bool> ById(string? id) =>
        n => n.IsMyScript && id is not null && ReferenceResolver.IdOf(n.Node!) == id;

    private static Func<NodeViewModel, bool> Same(NodeViewModel node) =>
        ReferenceResolver.IdOf(node.Node!) is { } id ? ById(id) : n => n.IsMyScript && n.Kind == node.Kind && n.Name == node.Name;

    private sealed class StaleEntryException() : Exception("My Scripts changed on disk; the tree was reloaded, please try again.");
}

/// <summary>"Change base…": re-points a customisation at another shared script, keeping its values.</summary>
public sealed partial class ChangeBaseViewModel(IReadOnlyList<BaseChoice> choices, Func<string, bool> apply) : ObservableObject
{
    public IReadOnlyList<BaseChoice> Choices { get; } = choices;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private BaseChoice? selected;

    public event Action? Closed;

    private bool CanApply() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private void Apply()
    {
        Closed?.Invoke();
        apply(Selected!.Reference);
    }

    [RelayCommand]
    private void Cancel() => Closed?.Invoke();
}
