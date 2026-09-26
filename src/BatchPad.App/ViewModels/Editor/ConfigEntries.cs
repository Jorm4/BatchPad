using System.Text.Json;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.App.ViewModels.Editor;

/// <summary>Finds and replaces entries in a freshly re-read config file, for saves through <see cref="ConfigWriter.Update"/>.</summary>
public static class ConfigEntries
{
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ConfigJson.Options), ConfigJson.Options)!;

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
        if (Find(scripts, n => original.Id is not null ? n.Id == original.Id : original.Path is not null && n.Path == original.Path)
            is ({ } owner, var found))
        {
            owner[found] = replacement;
            return true;
        }
        return false;
    }

    /// <summary>The folder's item list at <paramref name="folderPath"/>, or the top level when it no longer leads to a folder.</summary>
    public static List<TreeNode> FolderItems(List<TreeNode> scripts, IReadOnlyList<int>? folderPath) =>
        folderPath is not null && At(scripts, folderPath) is ({ } list, var index) && list[index] is FolderNode folder
            ? folder.Items
            : scripts;

    public static HashSet<string> Ids(IEnumerable<TreeNode> nodes) =>
        Walk(nodes).Select(n => n switch { RunnableNode r => r.Id, LinkNode l => l.Id, _ => null })
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<TreeNode> Walk(IEnumerable<TreeNode> nodes) =>
        nodes.SelectMany(n => n is FolderNode f ? Walk(f.Items).Prepend(n) : [n]);

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

    private static (List<TreeNode>?, int) Find(List<TreeNode> nodes, Func<ScriptNode, bool> match)
    {
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i] is ScriptNode script && match(script))
                return (nodes, i);
            if (nodes[i] is FolderNode folder && Find(folder.Items, match) is ({ } list, var index))
                return (list, index);
        }
        return (null, -1);
    }
}
