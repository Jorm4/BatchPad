using System.Text.Json.Nodes;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Customisation;

/// <summary>A My Scripts entry with its base applied (§3.7). A standalone entry is its own definition.</summary>
public sealed record ResolvedCustomisation(ScriptNode Entry)
{
    /// <summary>The base overlaid with the entry's fields; null when the base is missing.</summary>
    public RunnableNode? Definition { get; init; }

    /// <summary>The tree whose folder the definition's relative paths resolve against.</summary>
    public ScriptTree? DefinitionTree { get; init; }

    public required string Name { get; init; }
    public IReadOnlyDictionary<string, JsonNode?> Values { get; init; } = new Dictionary<string, JsonNode?>();
    public IReadOnlyDictionary<string, Dictionary<string, JsonNode?>> StepValues { get; init; } =
        new Dictionary<string, Dictionary<string, JsonNode?>>();

    /// <summary>Why the base could not be resolved; the entry keeps its data until re-pointed or deleted.</summary>
    public string? Problem { get; init; }

    public IReadOnlyList<string> ValueProblems { get; init; } = [];

    public bool IsBroken => Problem is not null;
    public bool IsCustomisation => Entry.Base is not null;
    public string? ExtraArgs => Entry.ExtraArgs;

    /// <exception cref="InvalidOperationException">The entry is broken or its definition is a workflow.</exception>
    public RunRequest ToRunRequest(LoadedWorkspace workspace) =>
        Definition is ScriptNode script && DefinitionTree is { } tree
            ? new RunRequest(workspace, tree, script) { Values = Values, ExtraArguments = ExtraArgs }
            : throw new InvalidOperationException(Problem ?? $"'{Name}' is a workflow; run it with WorkflowRunner.");
}
