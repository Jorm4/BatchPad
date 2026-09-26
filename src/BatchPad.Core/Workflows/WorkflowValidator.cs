using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Workflows;

public sealed class WorkflowException(string message) : Exception(message);

/// <summary>Finds workflows that run themselves, directly or through nested workflows (§4.1).</summary>
public static class WorkflowValidator
{
    public static IReadOnlyList<LoadError> FindCycles(IEnumerable<ScriptTree> trees, ReferenceResolver references) =>
        CycleFinder.FindAll<WorkflowNode>(trees, references, StepTargets,
            (workflow, cycle) => $"Workflow '{ScriptTree.DisplayName(workflow)}' runs itself: {cycle}.");

    public static string? CycleFrom(WorkflowNode workflow, ScriptTree tree, ReferenceResolver references) =>
        CycleFinder.From(workflow, tree, references, StepTargets);

    private static IEnumerable<string?> StepTargets(WorkflowNode workflow) => workflow.Steps.SelectMany(s => s.Leaves()).Select(s => s.Run);
}
