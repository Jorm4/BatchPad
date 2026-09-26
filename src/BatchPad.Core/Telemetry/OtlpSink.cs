using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace BatchPad.Core.Telemetry;

/// <summary>
/// OTLP/HTTP JSON traces: a run is a span, and a workflow is a trace whose steps are child spans. Trace and span ids
/// are derived from run ids, so a step sent before its workflow still lands in the workflow's trace.
/// </summary>
public sealed class OtlpSink(SinkConfig config, HttpMessageHandler? handler = null) : HttpSinkBase(config, handler)
{
    public const string TracesPath = "/v1/traces";
    private const int InternalSpan = 1;
    private const int StatusOk = 1;
    private const int StatusError = 2;

    protected override HttpRequestMessage CreateRequest(SinkConfig resolved, IReadOnlyList<TelemetryEvent> batch)
    {
        var endpoint = BaseUrl(resolved.Endpoint ?? resolved.Url, "OTLP endpoint");
        var url = endpoint.AbsolutePath.TrimEnd('/').EndsWith(TracesPath, StringComparison.Ordinal) ? endpoint : Append(endpoint, TracesPath);
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = Content(Payload(batch).ToJsonString(), "application/json") };
    }

    public static JsonObject Payload(IReadOnlyList<TelemetryEvent> batch) => new()
    {
        ["resourceSpans"] = new JsonArray([.. batch
            .GroupBy(e => (e.Machine, e.User, e.BatchPadVersion))
            .Select(group => (JsonNode)new JsonObject
            {
                ["resource"] = new JsonObject { ["attributes"] = Attributes(ResourceAttributes(group.Key)) },
                ["scopeSpans"] = new JsonArray(new JsonObject
                {
                    ["scope"] = new JsonObject { ["name"] = "batchpad" },
                    ["spans"] = new JsonArray([.. group.Select(Span)]),
                }),
            })]),
    };

    public static string TraceId(TelemetryEvent e) => HexId("trace:" + (e.ParentRunId ?? e.RunId), 16);
    public static string SpanId(string runId) => HexId("span:" + runId, 8);

    private static IEnumerable<(string, JsonNode?)> ResourceAttributes((string? Machine, string? User, string? Version) resource)
    {
        yield return ("service.name", Text("batchpad"));
        yield return ("service.version", Text(resource.Version));
        yield return ("host.name", Text(resource.Machine));
        yield return ("user.name", Text(resource.User));
    }

    private static JsonNode Span(TelemetryEvent e)
    {
        var start = UnixNanoseconds(e.Time);
        var span = new JsonObject
        {
            ["traceId"] = TraceId(e),
            ["spanId"] = SpanId(e.RunId),
        };
        if (e.ParentRunId is { } parent)
            span["parentSpanId"] = SpanId(parent);
        span["name"] = e.Script.Name;
        span["kind"] = InternalSpan;
        span["startTimeUnixNano"] = start.ToString(CultureInfo.InvariantCulture);
        span["endTimeUnixNano"] = (start + e.DurationMs * 1_000_000).ToString(CultureInfo.InvariantCulture);
        span["attributes"] = Attributes(SpanAttributes(e));
        var succeeded = e.Outcome == "exited" && e.ExitCode == 0;
        span["status"] = succeeded
            ? new JsonObject { ["code"] = StatusOk }
            : new JsonObject { ["code"] = StatusError, ["message"] = e.Outcome == "exited" ? $"exit code {e.ExitCode}" : e.Outcome };
        return span;
    }

    private static IEnumerable<(string, JsonNode?)> SpanAttributes(TelemetryEvent e)
    {
        yield return ("batchpad.run.id", Text(e.RunId));
        yield return ("batchpad.workspace.id", Text(e.Workspace.Id));
        yield return ("batchpad.workspace.name", Text(e.Workspace.Name));
        yield return ("vcs.ref.head.name", Text(e.Workspace.Branch));
        yield return ("vcs.ref.head.revision", Text(e.Workspace.Commit));
        yield return ("batchpad.script.tree", Text(e.Script.Tree));
        yield return ("batchpad.script.id", Text(e.Script.Id));
        yield return ("batchpad.script.kind", Text(e.Script.Kind));
        yield return ("batchpad.script.folder", Text(e.Script.Folder));
        yield return ("batchpad.script.tags", e.Script.Tags is { Count: > 0 } tags
            ? new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray([.. tags.Select(t => Text(t))]) } }
            : null);
        yield return ("batchpad.checkout.name", Text(e.Checkout?.Name));
        yield return ("batchpad.checkout.kind", Text(e.Checkout?.Kind));
        yield return ("batchpad.step.id", Text(e.StepId));
        yield return ("batchpad.trigger", Text(e.Trigger));
        yield return ("batchpad.outcome", Text(e.Outcome));
        yield return ("process.exit.code", Integer(e.ExitCode));
        yield return ("batchpad.queued_ms", Integer(e.QueuedMs));
        yield return ("batchpad.tests.passed", e.Tests is { } passed ? Integer(passed.Passed) : null);
        yield return ("batchpad.tests.failed", e.Tests is { } failed ? Integer(failed.Failed) : null);
        yield return ("batchpad.tests.skipped", e.Tests is { } skipped ? Integer(skipped.Skipped) : null);
        foreach (var (name, value) in e.Values ?? [])
            yield return ("batchpad.value." + name, Text(value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value?.ToJsonString() ?? "null"));
    }

    private static JsonArray Attributes(IEnumerable<(string Key, JsonNode? Value)> attributes) =>
        new([.. attributes.Where(a => a.Value is not null).Select(a => (JsonNode)new JsonObject { ["key"] = a.Key, ["value"] = a.Value })]);

    private static JsonObject? Text(string? value) => value is null ? null : new JsonObject { ["stringValue"] = value };

    private static JsonObject Integer(long value) => new() { ["intValue"] = value.ToString(CultureInfo.InvariantCulture) };

    private static long UnixNanoseconds(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;

    private static string HexId(string seed, int bytes) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed))[..bytes]);
}
