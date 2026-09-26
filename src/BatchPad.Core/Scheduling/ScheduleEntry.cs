using System.Text.Json.Nodes;
using BatchPad.Core.Customisation;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Scheduling;

/// <summary>The script or workflow a schedule runs, resolved in a loaded workspace.</summary>
public sealed record ScheduleTarget(LoadedWorkspace Workspace, ScriptTree Tree, RunnableNode Definition, string Reference)
{
    /// <summary>The history key of the node the reference names (a My Scripts entry, not its base).</summary>
    public required string NodeKey { get; init; }

    public required string Name { get; init; }
    public IReadOnlyDictionary<string, JsonNode?> Values { get; init; } = new Dictionary<string, JsonNode?>();
    public IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? StepValues { get; init; }
    public string? ExtraArguments { get; init; }

    /// <summary>Resolves <paramref name="reference"/> from <paramref name="from"/>, applying a My Scripts customisation.</summary>
    public static ScheduleTarget? Resolve(string reference, ScriptTree from, LoadedWorkspace workspace, out string? problem)
    {
        problem = null;
        var node = workspace.References.Resolve(reference, from);
        if (node is not RunnableNode runnable || workspace.References.TreeOf(node) is not { } tree)
        {
            problem = node is null ? $"'{reference}' was not found." : $"'{reference}' is not a script or workflow.";
            return null;
        }
        var nodeKey = tree.NodeKey(node)!;
        if (tree.Kind != TreeKind.MyScripts || runnable is not ScriptNode entry)
            return new ScheduleTarget(workspace, tree, runnable, reference) { NodeKey = nodeKey, Name = ScriptTree.DisplayName(runnable) };

        var resolved = new CustomisationResolver(workspace).Resolve(entry);
        if (resolved is not { Definition: { } definition, DefinitionTree: { } definitionTree })
        {
            problem = resolved.Problem;
            return null;
        }
        return new ScheduleTarget(workspace, definitionTree, definition, reference)
        {
            NodeKey = nodeKey,
            Name = resolved.Name,
            Values = resolved.Values,
            StepValues = resolved.StepValues,
            ExtraArguments = resolved.ExtraArgs,
        };
    }
}

/// <summary>A schedule as the scheduler runs it: a key unique across workspaces, and its target or why there is none.</summary>
public sealed record ScheduleEntry(string Key, Schedule Schedule)
{
    public ScheduleTarget? Target { get; init; }
    public string? Problem { get; init; }

    /// <summary>The tree the schedule's references are resolved from.</summary>
    public ScriptTree? From { get; init; }

    /// <summary>The workspace's user.json schedules, and global.json ones that are not tied to another workspace (§4.2).</summary>
    public static IReadOnlyList<ScheduleEntry> For(LoadedWorkspace workspace)
    {
        var entries = new List<ScheduleEntry>();
        foreach (var schedule in workspace.MyScripts.File.Schedules ?? [])
            entries.Add(Create($"{workspace.Id}:{schedule.Key}", schedule, workspace.MyScripts, workspace));
        foreach (var schedule in workspace.Global.File.Schedules ?? [])
        {
            if (schedule.Workspace is null || Names(workspace, schedule.Workspace))
                entries.Add(Create($"global:{schedule.Key}", schedule, workspace.Global, workspace));
        }
        return entries;
    }

    public static ScheduleEntry Create(string key, Schedule schedule, ScriptTree from, LoadedWorkspace workspace)
    {
        if (TriggerMath.Problem(schedule.Trigger) is { } triggerProblem)
            return new ScheduleEntry(key, schedule) { Problem = triggerProblem, From = from };
        var target = ScheduleTarget.Resolve(schedule.Target, from, workspace, out var problem);
        return new ScheduleEntry(key, schedule) { Target = target, Problem = problem, From = from };
    }

    private static bool Names(LoadedWorkspace workspace, string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(workspace.FilePath, StringComparison.OrdinalIgnoreCase)
            || full.Equals(Path.TrimEndingDirectorySeparator(workspace.Directory), StringComparison.OrdinalIgnoreCase);
    }
}
