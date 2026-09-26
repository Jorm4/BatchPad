using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Workflows;

public sealed class WorkflowException(string message) : Exception(message);

/// <summary>Finds workflows that run themselves, directly or through nested workflows (§4.1).</summary>
public static class WorkflowValidator
{
    public static IReadOnlyList<LoadError> FindCycles(IEnumerable<ScriptTree> trees, ReferenceResolver references) =>
        trees.SelectMany(tree => tree.AllNodes()
                .Select(n => n.Node)
                .OfType<WorkflowNode>()
                .Select(workflow => CycleFrom(workflow, tree, references) is { } cycle
                    ? new LoadError($"Workflow '{ScriptTree.DisplayName(workflow)}' runs itself: {cycle}.", tree.FilePath)
                    : null))
            .OfType<LoadError>()
            .ToList();

    /// <summary>The cycle as <c>a → b → a</c> when <paramref name="workflow"/> reaches itself; null otherwise.</summary>
    public static string? CycleFrom(WorkflowNode workflow, ScriptTree tree, ReferenceResolver references)
    {
        var visited = new HashSet<WorkflowNode>();
        return Search(workflow, tree, [ScriptTree.DisplayName(workflow)]);

        string? Search(WorkflowNode current, ScriptTree currentTree, List<string> path)
        {
            foreach (var step in current.Steps.SelectMany(s => s.Leaves()))
            {
                if (step.Run is not { } reference
                    || references.Resolve(reference, currentTree) is not WorkflowNode next)
                    continue;
                List<string> nextPath = [.. path, ScriptTree.DisplayName(next)];
                if (ReferenceEquals(next, workflow))
                    return string.Join(" → ", nextPath);
                if (visited.Add(next) && Search(next, references.TreeOf(next)!, nextPath) is { } cycle)
                    return cycle;
            }
            return null;
        }
    }
}
