using System.Text.Json.Nodes;
using BatchPad.Core.Customisation;
using BatchPad.Core.History;
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

    /// <summary>The target's values with the schedule's fixed ones over them.</summary>
    public Dictionary<string, JsonNode?> ValuesWith(Schedule schedule)
    {
        var values = new Dictionary<string, JsonNode?>(Values);
        foreach (var (name, value) in schedule.Values ?? [])
            values[name] = value?.DeepClone();
        return values;
    }

    public RunRecord RecordTemplate(string trigger) =>
        new() { NodeKey = NodeKey, Tree = Tree.Kind, NodeId = Definition.Id, Name = Name, Trigger = trigger };

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
    private const string GlobalPrefix = "global:";
    public const string OnlyTimeTriggersInWindows = "Only time triggers can run in Windows.";
    public const string PercentInWindows = "Can't run in Windows: the path or id contains %.";

    public bool IsGlobal => Key.StartsWith(GlobalPrefix, StringComparison.Ordinal);

    /// <summary>The machine-wide lock a run from the command line holds to apply <see cref="Schedule.Overlap"/>.</summary>
    public string OverlapLock => "batchpad-schedule:" + Key;
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
                entries.Add(Create(GlobalPrefix + schedule.Key, schedule, workspace.Global, workspace));
        }
        return entries;
    }

    public static ScheduleEntry Create(string key, Schedule schedule, ScriptTree from, LoadedWorkspace workspace)
    {
        if ((TriggerMath.Problem(schedule.Trigger) ?? WindowsProblem(key, schedule, from, workspace)) is { } scheduleProblem)
            return new ScheduleEntry(key, schedule) { Problem = scheduleProblem, From = from };
        var target = ScheduleTarget.Resolve(schedule.Target, from, workspace, out var problem);
        return new ScheduleEntry(key, schedule) { Target = target, Problem = problem, From = from };
    }

    private static string? WindowsProblem(string key, Schedule schedule, ScriptTree from, LoadedWorkspace workspace) =>
        schedule.RunIn != RunIn.Windows ? null
        : !schedule.Trigger.IsTimed ? OnlyTimeTriggersInWindows
        // Otherwise every workspace it loads in would register its own task.
        : from.Kind == TreeKind.Global && schedule.Workspace is null ? "A global schedule runs in Windows only when it names its workspace."
        // Task Scheduler expands %VAR% in a task's arguments.
        : key.Contains('%') || workspace.FilePath.Contains('%') ? PercentInWindows
        : null;

    private static bool Names(LoadedWorkspace workspace, string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Equals(workspace.FilePath, StringComparison.OrdinalIgnoreCase)
            || full.Equals(Path.TrimEndingDirectorySeparator(workspace.Directory), StringComparison.OrdinalIgnoreCase);
    }
}
