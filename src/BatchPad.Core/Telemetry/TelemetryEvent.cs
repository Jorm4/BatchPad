using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace BatchPad.Core.Telemetry;

/// <summary>One finished run, workflow or workflow step as sinks receive it (§4.4, schema 1).</summary>
public sealed record TelemetryEvent
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; } = CurrentSchema;
    public required string EventId { get; init; }
    public required string RunId { get; init; }
    public string? ParentRunId { get; init; }
    public string? StepId { get; init; }

    [JsonConverter(typeof(UtcMillisecondsConverter))]
    public DateTimeOffset Time { get; init; }

    public long QueuedMs { get; init; }
    public long DurationMs { get; init; }
    public required TelemetryWorkspace Workspace { get; init; }
    public required TelemetryScript Script { get; init; }
    public TelemetryCheckout? Checkout { get; init; }
    public required string Trigger { get; init; }
    public required string Outcome { get; init; }
    public int ExitCode { get; init; }
    public TelemetryTests? Tests { get; init; }
    public string? Machine { get; init; }
    public string? User { get; init; }
    public string? BatchPadVersion { get; init; }
    public Dictionary<string, JsonNode?>? Values { get; init; }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private sealed class UtcMillisecondsConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
    }
}

public sealed record TelemetryWorkspace(string Id, string Name)
{
    public string? Branch { get; init; }
    public string? Commit { get; init; }
}

public sealed record TelemetryScript(string Tree, string? Id, string Name, string Kind)
{
    public const string ScriptKind = "script";
    public const string WorkflowKind = "workflow";

    public string? Folder { get; init; }
    public List<string>? Tags { get; init; }
}

public sealed record TelemetryCheckout(string Name, string Kind);

public sealed record TelemetryTests(int Passed, int Failed, int Skipped);
