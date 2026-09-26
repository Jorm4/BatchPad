using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BatchPad.Core.Telemetry;

/// <summary>Elasticsearch <c>_bulk</c> NDJSON, one document per event with its event id as <c>_id</c>, so a resend overwrites.</summary>
public sealed class ElasticSink(SinkConfig config, HttpMessageHandler? handler = null) : HttpSinkBase(config, handler)
{
    public const string DefaultIndex = "batchpad-runs";

    protected override HttpRequestMessage CreateRequest(SinkConfig resolved, IReadOnlyList<TelemetryEvent> batch)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Append(BaseUrl(resolved.Url, "Elasticsearch url"), "/_bulk"))
        {
            Content = Content(Payload(batch, resolved.Index is { Length: > 0 } index ? index : DefaultIndex), "application/x-ndjson"),
        };
        if (resolved.ApiKey is { Length: > 0 } apiKey)
            request.Headers.Authorization = new AuthenticationHeaderValue("ApiKey", apiKey);
        else if (resolved.Username is { Length: > 0 } user)
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{resolved.Password}")));
        return request;
    }

    public static string Payload(IReadOnlyList<TelemetryEvent> batch, string index)
    {
        var text = new StringBuilder();
        foreach (var e in batch)
        {
            var action = new JsonObject { ["index"] = new JsonObject { ["_index"] = index, ["_id"] = e.EventId } };
            var document = new JsonObject { ["@timestamp"] = e.Time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) };
            foreach (var (name, value) in JsonSerializer.SerializeToNode(e, TelemetryEvent.Json)!.AsObject())
                document[name] = value?.DeepClone();
            text.Append(action.ToJsonString(TelemetryEvent.Json)).Append('\n').Append(document.ToJsonString(TelemetryEvent.Json)).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>A bulk answer is 200 even when documents failed; those are retried only when Elasticsearch was overloaded.</summary>
    protected override void CheckAnswer(string body)
    {
        JsonNode? answer;
        try
        {
            answer = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return;
        }
        if (answer?["errors"]?.GetValueKind() != JsonValueKind.True)
            return;
        var failures = (answer["items"]?.AsArray() ?? [])
            .Select(item => item?.AsObject().FirstOrDefault().Value)
            .Where(result => result?["error"] is not null)
            .ToList();
        var transient = failures.Any(f => f?["status"]?.GetValue<int>() is 429 or >= 500);
        var first = failures.FirstOrDefault()?["error"];
        var reason = first?["reason"]?.ToString() ?? first?["type"]?.ToString() ?? "unknown error";
        throw new TelemetrySendException($"Elasticsearch rejected {failures.Count} documents: {reason}", retry: transient);
    }
}
