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

    /// <summary>For a <c>global.json</c> library: the name in <c>global:&lt;library&gt;:id</c> references.</summary>
    public string? Library { get; init; }

    /// <summary>An included file or a library, shown as a sub-folder of its parent tree.</summary>
    public bool IsPart { get; init; }

    public List<ScriptTree> Parts { get; } = [];

    public string Label => File.Name ?? Path.GetFileNameWithoutExtension(FilePath);

    /// <summary>Where ids are unique: the tree kind, plus the library for a library and its includes.</summary>
    public string Scope => Library is null ? Kind.ToString() : $"{Kind}:{Library}";

    /// <summary>How the app and history key a node of this tree: its id, else its file; null when it has neither.</summary>
    public string? NodeKey(TreeNode? node, string? filePath = null) =>
        node is not null && ReferenceResolver.IdOf(node) is { } id ? $"{Scope}:id:{id}"
        : filePath is not null ? $"{Kind}:path:" + Path.GetFullPath(filePath, BaseDirectory).ToLowerInvariant()
        : null;

    public IEnumerable<ScriptTree> SelfAndParts() => Parts.SelectMany(p => p.SelfAndParts()).Prepend(this);

    /// <summary>The tree as shown: entries merged with discovered scripts. Filled by <see cref="Rescan"/>.</summary>
    public IReadOnlyList<TreeItem> Items { get; private set; } = [];

    public IReadOnlyList<ScriptFolder> ScriptFolders =>
        File.ScriptFolders ?? (Kind == TreeKind.Workspace && !IsPart ? ScriptFolderScanner.DefaultFolders : []);

    public IEnumerable<string> ScriptFolderDirectories =>
        ScriptFolders.Select(f => ScriptFolderScanner.FullPath(BaseDirectory, f));

    public void Rescan(IReadOnlySet<string>? seenPaths = null)
    {
        var items = TreeMerger.Merge(File.Scripts, BaseDirectory, ScriptFolderScanner.Scan(BaseDirectory, ScriptFolders), seenPaths);
        foreach (var part in Parts)
        {
            part.Rescan();
            var folder = new TreeItem { Node = new FolderNode { Folder = part.Label }, Name = part.Label, Part = part };
            folder.Children.AddRange(part.Items);
            items.Add(folder);
        }
        Items = items;
    }

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
