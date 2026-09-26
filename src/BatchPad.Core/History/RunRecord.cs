using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.History;

public static class RunTriggers
{
    public const string Manual = "manual";
    public const string Cli = "cli";
    public const string AfterRun = "afterRun";
    public static string Schedule(string scheduleId) => "schedule:" + scheduleId;
    public static string ResumedFrom(string stepId) => "resume:" + stepId;
}

/// <summary>One finished run as history keeps it (§3.9).</summary>
public sealed record RunRecord
{
    public const string Masked = SecretMasker.Placeholder;

    public string Id { get; init; } = "";

    /// <summary>The tree plus the node's id or full path, as the app keys its tree nodes.</summary>
    public required string NodeKey { get; init; }

    public TreeKind Tree { get; init; }
    public string? NodeId { get; init; }
    public string? Path { get; init; }
    public required string Name { get; init; }
    public string Command { get; init; } = "";
    public Dictionary<string, JsonNode?> Values { get; init; } = [];
    public string? ExtraArguments { get; init; }
    public string Trigger { get; init; } = RunTriggers.Manual;
    public DateTimeOffset StartedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public RunOutcome Outcome { get; init; }
    public int ExitCode { get; init; }

    [JsonIgnore]
    public bool Succeeded => Outcome == RunOutcome.Exited && ExitCode == 0;
}
