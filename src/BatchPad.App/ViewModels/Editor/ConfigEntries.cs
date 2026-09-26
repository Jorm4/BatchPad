using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.App.ViewModels.Editor;

/// <summary>Finds and replaces entries in a freshly re-read config file, for saves through <see cref="ConfigWriter.Update"/>.</summary>
public static class ConfigEntries
{
    public static IReadOnlyList<int>? IndexPath(IReadOnlyList<TreeNode> nodes, TreeNode target)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (ReferenceEquals(nodes[i], target))
                return [i];
            if (nodes[i] is FolderNode folder && IndexPath(folder.Items, target) is { } inner)
                return [i, .. inner];
        }
        return null;
    }

    /// <summary>
    /// Replaces the entry at <paramref name="indexPath"/> when it still is <paramref name="original"/> (same id and path),
    /// else the entry with the same id, or failing that the same path. False when none is found.
    /// </summary>
    public static bool Replace(List<TreeNode> scripts, IReadOnlyList<int>? indexPath, ScriptNode original, TreeNode replacement)
    {
        if (indexPath is not null && At(scripts, indexPath) is ({ } list, var index) && list[index] is ScriptNode s
            && s.Id == original.Id && s.Path == original.Path)
        {
            list[index] = replacement;
            return true;
        }
        return ReplaceFirst<ScriptNode>(scripts,
            n => original.Id is not null ? n.Id == original.Id : original.Path is not null && n.Path == original.Path, replacement);
    }

    public static bool ReplaceWorkflow(List<TreeNode> scripts, string id, WorkflowNode replacement) =>
        ReplaceFirst<WorkflowNode>(scripts, n => n.Id == id, replacement);

    private static bool ReplaceFirst<T>(List<TreeNode> nodes, Func<T, bool> match, TreeNode replacement) where T : TreeNode
    {
        if (Find(nodes, match) is not ({ } owner, var found))
            return false;
        owner[found] = replacement;
        return true;
    }

    /// <summary>The folder's item list at <paramref name="folderPath"/>, or the top level when it no longer leads to a folder.</summary>
    public static List<TreeNode> FolderItems(List<TreeNode> scripts, IReadOnlyList<int>? folderPath) =>
        folderPath is not null && At(scripts, folderPath) is ({ } list, var index) && list[index] is FolderNode folder
            ? folder.Items
            : scripts;

    public static HashSet<string> Ids(IEnumerable<TreeNode> nodes) =>
        nodes.Descendants().Select(n => n switch { RunnableNode r => r.Id, LinkNode l => l.Id, _ => null })
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids taken in <paramref name="tree"/>'s scope: the freshly read file's plus those of the files it includes or is included by.</summary>
    public static HashSet<string> Ids(IEnumerable<TreeNode> nodes, LoadedWorkspace workspace, ScriptTree tree)
    {
        var ids = Ids(nodes);
        foreach (var other in workspace.AllTrees.Where(t => t.Scope == tree.Scope && t != tree))
            ids.UnionWith(Ids(other.File.Scripts));
        return ids;
    }

    /// <summary>The list holding the entry at <paramref name="path"/> and its index there; (null, -1) when the path is stale.</summary>
    public static (List<TreeNode>? List, int Index) At(List<TreeNode> nodes, IReadOnlyList<int> path)
    {
        var list = nodes;
        for (var depth = 0; depth < path.Count; depth++)
        {
            if (path[depth] >= list.Count)
                return (null, -1);
            if (depth == path.Count - 1)
                return (list, path[depth]);
            if (list[path[depth]] is not FolderNode folder)
                return (null, -1);
            list = folder.Items;
        }
        return (null, -1);
    }

    private static (List<TreeNode>?, int) Find<T>(List<TreeNode> nodes, Func<T, bool> match) where T : TreeNode
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is T node && match(node))
                return (nodes, i);
            if (nodes[i] is FolderNode folder && Find(folder.Items, match) is ({ } list, var index))
                return (list, index);
        }
        return (null, -1);
    }
}
