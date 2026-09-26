using BatchPad.App.ViewModels.Editor;
using BatchPad.App.ViewModels.Workspace;
using BatchPad.Core.Config;
using BatchPad.Core.Detection;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatchPad.App.ViewModels;

public enum ExternalDropMode { Reference, Copy }

/// <summary>Tree drag-and-drop rules (§5, §5.1), kept out of the view so they can be tested without a mouse.</summary>
public sealed partial class DragDropHandler(MainViewModel main, MyScriptsViewModel myScripts) : ObservableObject
{
    /// <summary>Shared scripts drop onto My Scripts as customisations; My Scripts entries move within it, never into themselves.</summary>
    public static bool CanDrop(NodeViewModel dragged, NodeViewModel target)
    {
        if (target.Tree.Kind != TreeKind.MyScripts)
            return false;
        if (dragged.Tree.Kind != TreeKind.MyScripts)
            return MyScriptsViewModel.CanAdd(dragged);
        return dragged.IsMyScript && !target.IsWithin(dragged);
    }

    public bool Drop(NodeViewModel dragged, NodeViewModel target) =>
        CanDrop(dragged, target)
        && (dragged.Tree.Kind == TreeKind.MyScripts ? myScripts.Move(dragged, target) : myScripts.Add(dragged, target));

    /// <summary>Set while a script file dropped on a shared tree waits for "reference it where it is" or "copy it into the script folder".</summary>
    [ObservableProperty]
    private ExternalDropViewModel? pendingDrop;

    /// <summary>
    /// Files, folders and URLs dragged in from outside. Scripts become entries (standalone ones in My Scripts), anything
    /// else a link. On a shared tree, a script asks first unless <paramref name="mode"/> is given.
    /// </summary>
    public void DropExternal(IReadOnlyList<string> items, NodeViewModel target, ExternalDropMode? mode = null)
    {
        PendingDrop = null;
        if (items.Count == 0 || main.Workspace is null)
            return;
        var scripts = items.Where(IsScriptFile).ToList();
        if (target.Tree.Kind == TreeKind.MyScripts)
        {
            myScripts.AddNodes([.. items.Select(i => scripts.Contains(i) ? (TreeNode)StandaloneEntry(i) : Link(i, null))], target);
            return;
        }
        if (scripts.Count > 0 && mode is null)
        {
            PendingDrop = new ExternalDropViewModel(this, items, target, scripts.Count == 1 ? Path.GetFileName(scripts[0]) : $"{scripts.Count} scripts",
                target.Tree.ScriptFolders.Count > 0);
            return;
        }
        AddToSharedTree(items, scripts, target, mode ?? ExternalDropMode.Reference);
    }

    public static bool IsScriptFile(string item)
    {
        if (!File.Exists(item))
            return false;
        try
        {
            RunnerResolver.EffectiveRunner(new ScriptNode { Path = item });
            return true;
        }
        catch (RunException)
        {
            return false;
        }
    }

    private void AddToSharedTree(IReadOnlyList<string> items, List<string> scripts, NodeViewModel target, ExternalDropMode mode)
    {
        var tree = target.Tree;
        var folderPath = NewItemViewModel.NearestFolder(target) is { } folder ? ConfigEntries.IndexPath(tree.File.Scripts, folder) : null;
        var copies = new List<string>();
        try
        {
            if (mode == ExternalDropMode.Copy)
                foreach (var script in scripts)
                    copies.Add(CopyIntoScriptFolder(tree, script));
            var entries = items.Where(i => mode == ExternalDropMode.Reference || !scripts.Contains(i)).ToList();
            if (entries.Count > 0)
            {
                ConfigWriter.Update(tree.FilePath, file =>
                {
                    var ids = ConfigEntries.Ids(file.Scripts, main.Workspace!, tree);
                    var into = ConfigEntries.FolderItems(file.Scripts, folderPath);
                    foreach (var item in entries)
                    {
                        TreeNode node = scripts.Contains(item)
                            ? new ScriptNode { Path = RelativeTo(tree, item) }
                            : Link(item, tree);
                        SetId(node, ids);
                        into.Add(node);
                    }
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
        {
            myScripts.Error = ex.Message;
        }
        var selected = copies.Count > 0 ? copies[0] : scripts.FirstOrDefault() is { } first ? Path.GetFullPath(first) : null;
        main.Reload(selected is null ? null
            : n => n.Tree.FilePath == tree.FilePath && string.Equals(n.ScriptFullPath, selected, StringComparison.OrdinalIgnoreCase));
    }

    /// <returns>The copy's full path.</returns>
    private static string CopyIntoScriptFolder(ScriptTree tree, string script)
    {
        var name = Path.GetFileName(script);
        var folder = tree.ScriptFolders.FirstOrDefault(f => ScriptFolderScanner.Picks(f, name)) ?? tree.ScriptFolders[0];
        var destination = Path.Combine(ScriptFolderScanner.FullPath(tree.BaseDirectory, folder), name);
        if (File.Exists(destination))
            throw new IOException($"{name} already exists in {folder.Path}.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(script, destination);
        return destination;
    }

    private static ScriptNode StandaloneEntry(string script) =>
        new() { Name = Detector.ReadableName(script), Path = Path.GetFullPath(script) };

    /// <param name="tree">A local target is stored relative to this tree's folder; null keeps it absolute.</param>
    private static LinkNode Link(string item, ScriptTree? tree)
    {
        if (Uri.TryCreate(item, UriKind.Absolute, out var uri) && !uri.IsFile)
            return new LinkNode { Name = uri.Host.Length > 0 ? uri.Host : item, Url = item };
        var full = Path.GetFullPath(item);
        return new LinkNode
        {
            Name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)),
            Url = tree is null ? full : RelativeTo(tree, full),
        };
    }

    private static string RelativeTo(ScriptTree tree, string path) =>
        Path.GetRelativePath(tree.BaseDirectory, Path.GetFullPath(path)).Replace('\\', '/');

    internal static void SetId(TreeNode node, HashSet<string> ids)
    {
        switch (node)
        {
            case ScriptNode { Path: { } path } script when script.Name is null:
                ids.Add(script.Id = IdAssigner.FromFileName(path, ids));
                break;
            case RunnableNode runnable:
                ids.Add(runnable.Id = IdAssigner.FromName(runnable.Name ?? "script", ids));
                break;
            case LinkNode link:
                ids.Add(link.Id = IdAssigner.FromName(link.Name ?? "link", ids));
                break;
        }
    }

    internal void Complete(ExternalDropViewModel drop, ExternalDropMode? mode)
    {
        PendingDrop = null;
        if (mode is { } chosen)
            DropExternal(drop.Items, drop.Target, chosen);
    }
}

public sealed partial class ExternalDropViewModel(DragDropHandler handler, IReadOnlyList<string> items, NodeViewModel target,
    string what, bool canCopy) : ObservableObject
{
    public IReadOnlyList<string> Items { get; } = items;
    public NodeViewModel Target { get; } = target;
    public string Message { get; } = $"Add {what} to {NodeViewModel.RootLabel(target.Tree.Kind)}:";

    [RelayCommand]
    private void Reference() => handler.Complete(this, ExternalDropMode.Reference);

    private bool CanCopy() => canCopy;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy() => handler.Complete(this, ExternalDropMode.Copy);

    [RelayCommand]
    private void Cancel() => handler.Complete(this, null);
}
