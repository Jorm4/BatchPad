using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Telemetry;

/// <summary>Turns history records into telemetry events, applying the privacy switches.</summary>
public static class TelemetryEvents
{
    /// <param name="workspace">The workspace's id and name; its branch and commit come from the record.</param>
    /// <param name="salt">The per-install salt <see cref="TelemetryOptions.HashNames"/> hashes with.</param>
    public static TelemetryEvent From(RunRecord record, TelemetryWorkspace workspace, TelemetryOptions options, string salt = "")
    {
        string? Name(string? name) => options.HashNames && name is not null ? Hash(name, salt) : name;
        return new TelemetryEvent
        {
            EventId = EventIdOf(record.Id),
            RunId = record.Id,
            ParentRunId = record.ParentRunId,
            StepId = Name(record.StepId),
            Time = record.StartedAt,
            QueuedMs = record.QueuedMs,
            DurationMs = (long)record.Duration.TotalMilliseconds,
            Workspace = workspace with { Name = Name(workspace.Name)!, Branch = Name(record.Git?.Branch), Commit = record.Git?.Commit },
            Script = new TelemetryScript(JsonNamingPolicy.CamelCase.ConvertName(record.Tree.ToString()), Name(record.NodeId),
                Name(record.Name)!, IsWorkflow(record) ? TelemetryScript.WorkflowKind : TelemetryScript.ScriptKind)
            {
                Folder = Name(record.Folder),
                Tags = options.HashNames ? record.Tags?.Select(t => Hash(t, salt)).ToList() : record.Tags,
            },
            Checkout = record.Checkout is { } checkout
                ? new TelemetryCheckout(Name(checkout.Name)!, JsonNamingPolicy.CamelCase.ConvertName(checkout.Kind.ToString()))
                : null,
            Trigger = options.HashNames ? HashTrigger(record.Trigger, salt) : record.Trigger,
            Outcome = JsonNamingPolicy.CamelCase.ConvertName(record.Outcome.ToString()),
            ExitCode = record.ExitCode,
            Tests = record.Tests is { } tests ? new TelemetryTests(tests.Passed, tests.Failed, tests.Skipped) : null,
            Machine = options.Machine ? record.Machine : null,
            User = options.User ? record.User : null,
            BatchPadVersion = record.BatchPadVersion,
            Values = options.IncludeValues && record.Values.Count > 0
                ? record.Values.Where(v => !IsMasked(v.Value)).ToDictionary(v => v.Key, v => v.Value?.DeepClone())
                : null,
        };
    }

    public static TelemetryWorkspace WorkspaceOf(LoadedWorkspace workspace) =>
        new(workspace.Id, workspace.Workspace.File.Name ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(workspace.Directory)));

    /// <summary>Stable per run, so a re-sent event can be recognised as the same one.</summary>
    public static string EventIdOf(string runId) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("event:" + runId))[..16]).ToString();

    public static string Hash(string name, string salt) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes(name)))[..16];

    private static readonly string[] NamedTriggerPrefixes =
        [RunTriggers.Schedule(""), RunTriggers.AfterRunOf(""), RunTriggers.ResumedFrom("")];

    /// <summary>Hashes the schedule or step a trigger names, keeping its kind; agent names and plain triggers stay.</summary>
    public static string HashTrigger(string trigger, string salt) =>
        NamedTriggerPrefixes.FirstOrDefault(p => trigger.StartsWith(p, StringComparison.Ordinal)) is { } prefix
            ? prefix + Hash(trigger[prefix.Length..], salt)
            : trigger;

    // A workflow's own record has no script file and no command line.
    private static bool IsWorkflow(RunRecord record) => record.Path is null && record.Command.Length == 0;

    private static bool IsMasked(JsonNode? value) =>
        value is JsonValue json && json.TryGetValue<string>(out var text) && text == RunRecord.Masked;
}
