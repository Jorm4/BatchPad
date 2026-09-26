using System.Text.Json;

namespace BatchPad.Core.Telemetry;

/// <summary>A generic webhook: the batch POSTed as a JSON array, with the configured headers.</summary>
public sealed class HttpSink(SinkConfig config, HttpMessageHandler? handler = null) : HttpSinkBase(config, handler)
{
    protected override HttpRequestMessage CreateRequest(SinkConfig resolved, IReadOnlyList<TelemetryEvent> batch) =>
        new(HttpMethod.Post, BaseUrl(resolved.Url, "HTTP sink url"))
        {
            Content = Content(JsonSerializer.Serialize(batch, TelemetryEvent.Json), "application/json"),
        };
}
