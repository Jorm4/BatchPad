using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using BatchPad.Core.History;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>A finished run as agents see it, from <c>run --json</c> or the MCP server (§4.5).</summary>
public sealed record RunSummary
{
    public required string RunId { get; init; }
    public string? Id { get; init; }
    public required string Name { get; init; }
    public RunOutcome Outcome { get; init; }
    public bool Succeeded { get; init; }
    public int ExitCode { get; init; }
    public long DurationMs { get; init; }
    public long QueuedMs { get; init; }
    public required string LogPath { get; init; }
    public Checkout? Checkout { get; init; }
    public TestSummary? Tests { get; init; }
    public List<ErrorLine> Errors { get; init; } = [];
    public List<RunSummary> Steps { get; init; } = [];

    /// <summary>
    /// Summarises <paramref name="record"/>; its steps are <paramref name="steps"/>, or else the records linked to it
    /// by <see cref="RunRecord.ParentRunId"/>.
    /// </summary>
    public static RunSummary From(RunRecord record, HistoryStore store, string? id = null, IEnumerable<RunRecord>? steps = null) => new()
    {
        RunId = record.Id,
        Id = id ?? record.StepId ?? record.NodeId,
        Name = record.Name,
        Outcome = record.Outcome,
        Succeeded = record.Succeeded,
        ExitCode = record.ExitCode,
        DurationMs = (long)record.Duration.TotalMilliseconds,
        QueuedMs = record.QueuedMs,
        LogPath = store.LogPath(record),
        Checkout = record.Checkout,
        Tests = record.Tests,
        Errors = record.Errors ?? [],
        Steps = [.. (steps ?? store.Recent().Where(r => r.ParentRunId == record.Id && r.Id != record.Id))
            .OrderBy(r => r.StartedAt)
            .Select(r => From(r, store, steps: []))],
    };
}

public static class RunResultJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(RunSummary summary) => JsonSerializer.Serialize(summary, Options);
}
