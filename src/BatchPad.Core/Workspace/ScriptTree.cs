using BatchPad.Core.Discovery;
using BatchPad.Core.Model;

namespace BatchPad.Core.Workspace;

public enum TreeKind { MyScripts, Workspace, Global }

public sealed record LoadError(string Message, string? FilePath);

/// <summary>One of the three trees (§2), backed by the config file it is saved to.</summary>
public sealed class ScriptTree(TreeKind kind, string filePath, WorkspaceFile file)
{
    public TreeKind Kind { get; } = kind;
    public string FilePath { get; } = filePath;
    public WorkspaceFile File { get; } = file;
    public string BaseDirectory => Path.GetDirectoryName(FilePath)!;

    /// <summary>The tree as shown: entries merged with discovered scripts. Filled by <see cref="Rescan"/>.</summary>
    public IReadOnlyList<TreeItem> Items { get; private set; } = [];

    public IReadOnlyList<ScriptFolder> ScriptFolders =>
        File.ScriptFolders ?? (Kind == TreeKind.Workspace ? ScriptFolderScanner.DefaultFolders : []);

    public IEnumerable<string> ScriptFolderDirectories =>
        ScriptFolders.Select(f => ScriptFolderScanner.FullPath(BaseDirectory, f));

    public void Rescan(IReadOnlySet<string>? seenPaths = null) =>
        Items = TreeMerger.Merge(File.Scripts, BaseDirectory, ScriptFolderScanner.Scan(BaseDirectory, ScriptFolders), seenPaths);

    /// <summary>Every node with its folder location, e.g. <c>Build › Build</c>.</summary>
    public IEnumerable<(TreeNode Node, string Location)> AllNodes() => Walk(File.Scripts, "");

    private static IEnumerable<(TreeNode, string)> Walk(IEnumerable<TreeNode> nodes, string parent)
    {
        foreach (var node in nodes)
        {
            var location = parent.Length == 0 ? DisplayName(node) : $"{parent} › {DisplayName(node)}";
            yield return (node, location);
            if (node is FolderNode folder)
                foreach (var child in Walk(folder.Items, location))
                    yield return child;
        }
    }

    public static string DisplayName(TreeNode node) => node switch
    {
        FolderNode f => f.Folder ?? "",
        LinkNode l => l.Name ?? l.Url ?? "",
        ScriptNode s => s.Name ?? s.Id ?? s.Path ?? s.Command ?? s.Module ?? "",
        RunnableNode r => r.Name ?? r.Id ?? "",
        _ => "",
    };
}
