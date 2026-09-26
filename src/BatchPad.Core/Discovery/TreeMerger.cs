using BatchPad.Core.Config;
using BatchPad.Core.Detection;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Discovery;

/// <summary>A node as shown in the tree: an explicit config entry, a discovered script, or both.</summary>
public sealed class TreeItem
{
    /// <summary>The config entry, or for a discovered script without one a fresh node that is not in any file.</summary>
    public required TreeNode Node { get; init; }
    public required string Name { get; init; }
    public string? ScriptPath { get; init; }
    public bool HasEntry { get; init; }
    public bool IsDiscovered { get; init; }
    public bool IsOrphan { get; init; }
    public bool IsNew { get; init; }

    /// <summary>Set on the folder that shows an included file or library; its children belong to that tree.</summary>
    public ScriptTree? Part { get; init; }
    public List<TreeItem> Children { get; } = [];
}

/// <summary>Merges discovered scripts with explicit entries keyed by <c>path</c> (§3.10).</summary>
public static class TreeMerger
{
    /// <param name="seenPaths">Paths already opened once; null marks nothing as New.</param>
    public static List<TreeItem> Merge(
        IEnumerable<TreeNode> entries, string baseDirectory, IEnumerable<DiscoveredScript> discovered,
        IReadOnlySet<string>? seenPaths = null, PathPolicy? paths = null)
    {
        var unclaimed = discovered.ToDictionary(d => d.RelativePath, StringComparer.OrdinalIgnoreCase);
        var items = MergeEntries(entries, baseDirectory, unclaimed, paths ?? PathPolicy.Unrestricted);

        var discoveredRoot = new List<TreeItem>();
        foreach (var script in unclaimed.Values.OrderBy(s => s.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            FolderFor(discoveredRoot, script.TreeFolders).Add(new TreeItem
            {
                Node = new ScriptNode { Path = script.RelativePath },
                Name = Detector.ReadableName(script.RelativePath),
                ScriptPath = script.RelativePath,
                IsDiscovered = true,
                IsNew = seenPaths is not null && !seenPaths.Contains(script.RelativePath),
            });
        }
        items.AddRange(discoveredRoot.OrderBy(i => i.Node is not FolderNode));
        return items;
    }

    private static List<TreeItem> MergeEntries(
        IEnumerable<TreeNode> entries, string baseDirectory, Dictionary<string, DiscoveredScript> unclaimed, PathPolicy paths)
    {
        var items = new List<TreeItem>();
        foreach (var node in entries)
        {
            if (node is FolderNode folder)
            {
                var folderItem = new TreeItem { Node = folder, Name = folder.Folder ?? "", HasEntry = true };
                folderItem.Children.AddRange(MergeEntries(folder.Items, baseDirectory, unclaimed, paths));
                items.Add(folderItem);
                continue;
            }

            var key = node is ScriptNode { Path: { } path } && !path.Contains("${")
                ? ScriptFolderScanner.RelativeKey(baseDirectory, Path.GetFullPath(Path.Combine(baseDirectory, path)))
                : null;
            var isDiscovered = key is not null && unclaimed.Remove(key);
            if (node is RunnableNode { Hidden: true })
                continue;

            var script = node as ScriptNode;
            var fullPath = key is null ? null : Path.Combine(baseDirectory, key);
            items.Add(new TreeItem
            {
                Node = node,
                Name = script is { Name: null, Path: { } scriptPath } ? Detector.ReadableName(scriptPath) : ScriptTree.DisplayName(node),
                ScriptPath = key,
                HasEntry = true,
                IsDiscovered = isDiscovered,
                IsOrphan = fullPath is not null && !isDiscovered && script!.Runner != Runner.Exe
                    && paths.Problem(Path.GetFullPath(fullPath)) is null && !File.Exists(fullPath),
            });
        }
        return items;
    }

    private static List<TreeItem> FolderFor(List<TreeItem> root, IReadOnlyList<string> folders)
    {
        var level = root;
        foreach (var name in folders)
        {
            var existing = level.FirstOrDefault(i => i.Node is FolderNode && i.Name == name);
            if (existing is null)
            {
                existing = new TreeItem { Node = new FolderNode { Folder = name }, Name = name };
                level.Add(existing);
            }
            level = existing.Children;
        }
        return level;
    }
}
