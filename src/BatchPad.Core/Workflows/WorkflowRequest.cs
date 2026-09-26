using System.Text.Json.Nodes;
using BatchPad.Core.Customisation;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Workflows;

/// <summary>A workflow to run, with the tree it is defined in (bare step references resolve there).</summary>
public sealed record WorkflowRequest(ScriptTree Tree, WorkflowNode Workflow)
{
    public IReadOnlyDictionary<string, JsonNode?>? Values { get; init; }

    /// <summary>Per-step value overrides by step id, as a customisation's <c>stepValues</c> (§3.7).</summary>
    public IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? StepValues { get; init; }

    public IEnumerable<KeyValuePair<string, string>>? BaseEnvironment { get; init; }

    /// <summary>The script a <c>dependsOn</c> chain ends with, run as requested by the workflow's last step.</summary>
    public RunRequest? Target { get; init; }

    public bool Unattended { get; init; }
    public bool Confirmed { get; init; }

    public WorkflowResume? Resume { get; init; }

    /// <summary>The earlier run's request again, starting at <paramref name="stepId"/> with the results of the steps before it.</summary>
    public static WorkflowRequest ResumeFrom(WorkflowRun previous, string stepId) =>
        previous.Request with { Resume = new WorkflowResume(stepId, new Dictionary<string, IReadOnlyDictionary<string, string>>(previous.StepResults)) };

    public static WorkflowRequest From(ResolvedCustomisation customisation) =>
        customisation is { Definition: WorkflowNode workflow, DefinitionTree: { } tree }
            ? new WorkflowRequest(tree, workflow) { Values = customisation.Values, StepValues = customisation.StepValues }
            : throw new InvalidOperationException(customisation.Problem ?? $"'{customisation.Name}' is not a workflow.");
}

/// <summary>Where a re-run starts (§4.1); the steps before it keep their earlier results and outputs.</summary>
public sealed record WorkflowResume(string StepId, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> StepResults);
