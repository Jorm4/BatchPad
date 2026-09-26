using BatchPad.Core.Model;

namespace BatchPad.Core.Workspace;

/// <summary>Resolves <c>&lt;tree&gt;:&lt;id&gt;</c> references (§3.2); a bare id means the referring node's own tree.</summary>
public sealed class ReferenceResolver
{
    private readonly Dictionary<string, Dictionary<string, TreeNode>> idsByScope = [];
    private readonly Dictionary<TreeNode, ScriptTree> treeByNode = new(ReferenceEqualityComparer.Instance);

    public static ReferenceResolver Build(IEnumerable<ScriptTree> trees, ICollection<LoadError> errors)
    {
        var resolver = new ReferenceResolver();
        var locationsByScope = new Dictionary<string, Dictionary<string, (string Location, ScriptTree Tree)>>();
        foreach (var root in trees)
            foreach (var tree in root.SelfAndParts())
            {
                if (!resolver.idsByScope.TryGetValue(tree.Scope, out var ids))
                {
                    resolver.idsByScope[tree.Scope] = ids = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
                    locationsByScope[tree.Scope] = new(StringComparer.Ordinal);
                }
                var locations = locationsByScope[tree.Scope];
                foreach (var (node, location) in tree.AllNodes())
                {
                    resolver.treeByNode[node] = tree;
                    if (IdOf(node) is not { Length: > 0 } id)
                        continue;
                    if (locations.TryGetValue(id, out var first))
                    {
                        errors.Add(new LoadError(DuplicateMessage(id, tree, location, first.Tree, first.Location, root), tree.FilePath));
                        continue;
                    }
                    ids[id] = node;
                    locations[id] = (location, tree);
                }
            }
        return resolver;
    }

    private static string DuplicateMessage(string id, ScriptTree tree, string location, ScriptTree firstTree, string firstLocation, ScriptTree root)
    {
        if (ReferenceEquals(tree, firstTree))
            return $"Duplicate id '{id}' in {tree.Scope}: '{firstLocation}' and '{location}'.";
        string FileOf(ScriptTree t) => Path.GetRelativePath(root.BaseDirectory, t.FilePath);
        return $"Duplicate id '{id}' in {tree.Scope}: '{firstLocation}' in {FileOf(firstTree)} and '{location}' in {FileOf(tree)}.";
    }

    public TreeNode? Resolve(string reference, TreeKind from) => Resolve(reference, from.ToString());

    public TreeNode? Resolve(string reference, ScriptTree from) => Resolve(reference, from.Scope);

    private TreeNode? Resolve(string reference, string fromScope) =>
        Parse(reference, fromScope) is { } parsed
        && idsByScope.TryGetValue(parsed.Scope, out var ids)
        && ids.TryGetValue(parsed.Id, out var node)
            ? node
            : null;

    public ScriptTree? TreeOf(TreeNode node) => treeByNode.GetValueOrDefault(node);

    /// <summary>A reference to <paramref name="node"/> that resolves from any tree; a bare id in My Scripts.</summary>
    public string? QualifiedReference(TreeNode node) =>
        TreeOf(node) is { } tree && IdOf(node) is { } id ? Qualified(tree, id) : null;

    public static string Qualified(ScriptTree tree, string id) => tree.Kind switch
    {
        TreeKind.Workspace => $"workspace:{id}",
        TreeKind.Global => tree.Library is null ? $"global:{id}" : $"global:{tree.Library}:{id}",
        _ => id,
    };

    private static (string Scope, string Id)? Parse(string reference, string fromScope) =>
        reference.Split(':') switch
        {
            [var id] => (fromScope, id),
            ["workspace", var id] => (nameof(TreeKind.Workspace), id),
            ["global", var id] => (nameof(TreeKind.Global), id),
            ["global", var library, var id] => ($"{nameof(TreeKind.Global)}:{library}", id),
            _ => null,
        };

    public static string? IdOf(TreeNode node) => node switch
    {
        RunnableNode r => r.Id,
        LinkNode l => l.Id,
        _ => null,
    };
}
