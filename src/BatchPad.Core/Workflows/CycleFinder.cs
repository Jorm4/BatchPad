using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Workflows;

internal static class CycleFinder
{
    public static IReadOnlyList<LoadError> FindAll<T>(IEnumerable<ScriptTree> trees, ReferenceResolver references,
        Func<T, IEnumerable<string?>> referencesOf, Func<T, string, string> describe) where T : RunnableNode =>
        trees.SelectMany(tree => tree.AllNodes()
                .Select(n => n.Node)
                .OfType<T>()
                .Select(node => From(node, tree, references, referencesOf) is { } cycle
                    ? new LoadError(describe(node, cycle), tree.FilePath)
                    : null))
            .OfType<LoadError>()
            .ToList();

    /// <summary>The cycle as <c>a → b → a</c> when <paramref name="start"/> reaches itself; null otherwise.</summary>
    public static string? From<T>(T start, ScriptTree tree, ReferenceResolver references, Func<T, IEnumerable<string?>> referencesOf)
        where T : RunnableNode
    {
        var visited = new HashSet<T>();
        return Search(start, tree, [ScriptTree.DisplayName(start)]);

        string? Search(T current, ScriptTree currentTree, List<string> path)
        {
            foreach (var reference in referencesOf(current).OfType<string>())
            {
                if (references.Resolve(reference, currentTree) is not T next)
                    continue;
                List<string> nextPath = [.. path, ScriptTree.DisplayName(next)];
                if (ReferenceEquals(next, start))
                    return string.Join(" → ", nextPath);
                if (visited.Add(next) && Search(next, references.TreeOf(next)!, nextPath) is { } cycle)
                    return cycle;
            }
            return null;
        }
    }
}
