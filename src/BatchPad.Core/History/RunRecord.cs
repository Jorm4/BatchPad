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
    private const string SchedulePrefix = "schedule:";
    private const string AfterRunPrefix = AfterRun + ":";

    public static string Schedule(string scheduleKey) => SchedulePrefix + scheduleKey;
    public static string AfterRunOf(string scheduleKey) => AfterRunPrefix + scheduleKey;
    public static string ResumedFrom(string stepId) => "resume:" + stepId;
    public static string Agent(string name) => "agent:" + name;

    /// <summary>The schedule that started a run with this trigger, whether timed, event or <c>afterRun</c>.</summary>
    public static string? ScheduleKey(string trigger) =>
        trigger.StartsWith(SchedulePrefix, StringComparison.Ordinal) ? trigger[SchedulePrefix.Length..]
        : trigger.StartsWith(AfterRunPrefix, StringComparison.Ordinal) ? trigger[AfterRunPrefix.Length..]
        : null;
}

/// <summary>One finished run as history keeps it (§3.9). Fields added later are optional, so older records still load.</summary>
public sealed record RunRecord
{
    public const string Masked = SecretMasker.Placeholder;
    public const int MaxErrors = 50;
    public const int MaxTextLength = 500;

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

    public string? ParentRunId { get; init; }
    public string? StepId { get; init; }
    public long QueuedMs { get; init; }

    /// <summary>The node's folders in the tree, joined by <c>/</c>; null at the top level.</summary>
    public string? Folder { get; init; }
    public List<string>? Tags { get; init; }
    public GitInfo? Git { get; init; }
    public Checkout? Checkout { get; init; }
    public TestSummary? Tests { get; init; }
    public List<ErrorLine>? Errors { get; init; }
    public string? Machine { get; init; }
    public string? User { get; init; }
    public string? BatchPadVersion { get; init; }

    [JsonIgnore]
    public bool Succeeded => Outcome == RunOutcome.Exited && ExitCode == 0;

    /// <summary>A new id: its time prefix keeps the files in start order.</summary>
    public static string NewId(DateTimeOffset startedAt) => $"{startedAt.UtcDateTime:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}"[..28];
}

public sealed record TestTiming(string Name, double Seconds);

public sealed record TestSummary(int Passed, int Failed, int Skipped, List<string> FailedNames, List<TestTiming> Slowest)
{
    public const int MaxFailedNames = 50;
    public const int MaxSlowest = 10;
}

/// <summary>An error-pattern or stderr line, with the source location it names when that file exists.</summary>
public sealed record ErrorLine(string Text, string? File = null, int Line = 0, int Column = 0);
