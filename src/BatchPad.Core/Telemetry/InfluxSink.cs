using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace BatchPad.Core.Telemetry;

/// <summary>InfluxDB v2 write API, line protocol: measurement <c>batchpad_run</c>, nanosecond timestamps.</summary>
public sealed class InfluxSink(SinkConfig config, HttpMessageHandler? handler = null) : HttpSinkBase(config, handler)
{
    public const string Measurement = "batchpad_run";

    protected override HttpRequestMessage CreateRequest(SinkConfig resolved, IReadOnlyList<TelemetryEvent> batch)
    {
        if (string.IsNullOrWhiteSpace(resolved.Org) || string.IsNullOrWhiteSpace(resolved.Bucket))
            throw new TelemetrySendException("The InfluxDB sink needs an org and a bucket.", retry: false);
        var baseUrl = BaseUrl(resolved.Url, "InfluxDB url");
        var url = new Uri(Append(baseUrl, "/api/v2/write").AbsoluteUri
            + $"?org={Uri.EscapeDataString(resolved.Org)}&bucket={Uri.EscapeDataString(resolved.Bucket)}&precision=ns");
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Content(Payload(batch), "text/plain") };
        if (resolved.Token is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        return request;
    }

    public static string Payload(IReadOnlyList<TelemetryEvent> batch)
    {
        var text = new StringBuilder();
        foreach (var e in batch)
        {
            text.Append(Measurement);
            foreach (var (key, value) in new[]
                     {
                         ("folder", e.Script.Folder), ("outcome", e.Outcome), ("script", e.Script.Name),
                         ("trigger", e.Trigger), ("workspace", e.Workspace.Name),
                     })
                if (!string.IsNullOrEmpty(value))
                    text.Append(',').Append(key).Append('=').Append(EscapeTag(value));
            text.Append(CultureInfo.InvariantCulture, $" duration_ms={e.DurationMs}i,queued_ms={e.QueuedMs}i,exit_code={e.ExitCode}i");
            if (e.Tests is { } tests)
                text.Append(CultureInfo.InvariantCulture, $",tests_failed={tests.Failed}i");
            text.Append(' ').Append((e.Time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100).Append('\n');
        }
        return text.ToString();
    }

    public static string EscapeTag(string value) => value
        .Replace("\r", "").Replace('\n', ' ')
        .Replace(",", "\\,").Replace("=", "\\=").Replace(" ", "\\ ");
}
