using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Workflows;

/// <summary><c>dependsOn</c> (§4 "Prerequisites"): a script's prerequisites run first, as an implicit workflow ending with the script.</summary>
public static class Prerequisites
{
    public sealed record Prerequisite(string Reference, RunnableNode? Node);

    /// <summary>The prerequisites in run order, their own prerequisites first, each once; unresolved references are kept so the run reports them.</summary>
    public static IReadOnlyList<Prerequisite> ChainOf(ScriptNode script, ScriptTree tree, ReferenceResolver references)
    {
        var chain = new List<Prerequisite>();
        var seen = new HashSet<TreeNode>([script]);
        Visit(script, tree);
        return chain;

        void Visit(ScriptNode current, ScriptTree currentTree)
        {
            foreach (var reference in current.DependsOn ?? [])
            {
                if (references.Resolve(reference, currentTree) is not RunnableNode node)
                {
                    chain.Add(new Prerequisite(reference, null));
                    continue;
                }
                if (!seen.Add(node))
                    continue;
                if (node is ScriptNode prerequisite)
                    Visit(prerequisite, references.TreeOf(node)!);
                chain.Add(new Prerequisite(references.QualifiedReference(node)!, node));
            }
        }
    }

    /// <summary>The implicit workflow for <paramref name="request"/>; null when its script has no <c>dependsOn</c>.</summary>
    public static WorkflowRequest? WorkflowFor(RunRequest request)
    {
        if (request.Script.DependsOn is not { Count: > 0 })
            return null;
        var chain = ChainOf(request.Script, request.Tree, request.Workspace.References);
        var workflow = new WorkflowNode
        {
            Id = request.Script.Id,
            Name = ScriptTree.DisplayName(request.Script),
            Params = request.Script.Params,
            Steps = [.. chain.Select(p => new WorkflowStep { Run = p.Reference }), new WorkflowStep { Id = ScriptTree.DisplayName(request.Script) }],
        };
        return new WorkflowRequest(request.Tree, workflow)
        {
            Values = request.Values,
            BaseEnvironment = request.BaseEnvironment,
            Target = request,
            Unattended = request.Unattended,
            Confirmed = request.Confirmed,
        };
    }

    public static IReadOnlyList<LoadError> FindCycles(IEnumerable<ScriptTree> trees, ReferenceResolver references) =>
        trees.SelectMany(tree => tree.AllNodes()
                .Select(n => n.Node)
                .OfType<ScriptNode>()
                .Select(script => CycleFrom(script, tree, references) is { } cycle
                    ? new LoadError($"'{ScriptTree.DisplayName(script)}' depends on itself: {cycle}.", tree.FilePath)
                    : null))
            .OfType<LoadError>()
            .ToList();

    /// <summary>The cycle as <c>a → b → a</c> when <paramref name="script"/>'s prerequisites reach it again; null otherwise.</summary>
    public static string? CycleFrom(ScriptNode script, ScriptTree tree, ReferenceResolver references)
    {
        var visited = new HashSet<ScriptNode>();
        return Search(script, tree, [ScriptTree.DisplayName(script)]);

        string? Search(ScriptNode current, ScriptTree currentTree, List<string> path)
        {
            foreach (var reference in current.DependsOn ?? [])
            {
                if (references.Resolve(reference, currentTree) is not ScriptNode next)
                    continue;
                List<string> nextPath = [.. path, ScriptTree.DisplayName(next)];
                if (ReferenceEquals(next, script))
                    return string.Join(" → ", nextPath);
                if (visited.Add(next) && Search(next, references.TreeOf(next)!, nextPath) is { } cycle)
                    return cycle;
            }
            return null;
        }
    }
}
