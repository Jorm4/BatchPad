using BatchPad.Core.Model;

namespace BatchPad.Core.Workspace;

/// <summary>Resolves <c>&lt;tree&gt;:&lt;id&gt;</c> references (§3.2); a bare id means the referring node's own tree.</summary>
public sealed class ReferenceResolver
{
    private readonly Dictionary<TreeKind, Dictionary<string, TreeNode>> idsByTree = [];

    public static ReferenceResolver Build(IEnumerable<ScriptTree> trees, ICollection<LoadError> errors)
    {
        var resolver = new ReferenceResolver();
        foreach (var tree in trees)
        {
            var ids = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
            var locations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (node, location) in tree.AllNodes())
            {
                if (IdOf(node) is not { Length: > 0 } id)
                    continue;
                if (locations.TryGetValue(id, out var firstLocation))
                {
                    errors.Add(new LoadError(
                        $"Duplicate id '{id}' in {tree.Kind}: '{firstLocation}' and '{location}'.", tree.FilePath));
                    continue;
                }
                ids[id] = node;
                locations[id] = location;
            }
            resolver.idsByTree[tree.Kind] = ids;
        }
        return resolver;
    }

    public TreeNode? Resolve(string reference, TreeKind from) =>
        Parse(reference, from) is { } parsed
        && idsByTree.TryGetValue(parsed.Tree, out var ids)
        && ids.TryGetValue(parsed.Id, out var node)
            ? node
            : null;

    /// <summary>Null for an unknown tree name, or a <c>global:&lt;library&gt;:id</c> reference (libraries are not loaded yet).</summary>
    public static (TreeKind Tree, string Id)? Parse(string reference, TreeKind from)
    {
        var parts = reference.Split(':');
        return parts switch
        {
            [var id] => (from, id),
            ["workspace", var id] => (TreeKind.Workspace, id),
            ["global", var id] => (TreeKind.Global, id),
            _ => null,
        };
    }

    public static string? IdOf(TreeNode node) => node switch
    {
        RunnableNode r => r.Id,
        LinkNode l => l.Id,
        _ => null,
    };
}
