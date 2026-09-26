using System.Net;
using System.Text;

namespace BatchPad.Core.Telemetry;

/// <summary>What the HTTP-based sinks share: one client with a 10 s timeout, http(s)-only URLs and answer checking.</summary>
public abstract class HttpSinkBase(SinkConfig config, HttpMessageHandler? handler) : ITelemetrySink, IDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client = handler is null
        ? new HttpClient { Timeout = Timeout }
        : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout };

    public async Task SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken)
    {
        var resolved = config.Resolve();
        using var request = CreateRequest(resolved, batch);
        foreach (var (name, value) in resolved.Headers ?? [])
            if (!request.Headers.TryAddWithoutValidation(name, value))
                request.Content?.Headers.TryAddWithoutValidation(name, value);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new TelemetrySendException($"{(int)response.StatusCode} {response.ReasonPhrase}: {Shorten(body)}".TrimEnd(' ', ':'),
                retry: IsTransient(response.StatusCode));
        CheckAnswer(body);
    }

    protected abstract HttpRequestMessage CreateRequest(SinkConfig resolved, IReadOnlyList<TelemetryEvent> batch);

    /// <summary>Throws when a 2xx answer still reports a failure.</summary>
    protected virtual void CheckAnswer(string body)
    {
    }

    public static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    protected static Uri BaseUrl(string? url, string setting)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new TelemetrySendException($"The {setting} must be an http or https URL.", retry: false);
        return uri;
    }

    protected static Uri Append(Uri baseUrl, string path) =>
        new(baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/') + path + baseUrl.Query);

    protected static StringContent Content(string text, string mediaType) =>
        new(text, Encoding.UTF8, mediaType);

    private static string Shorten(string text) => text.Length <= 200 ? text.Trim() : text[..200].Trim() + "…";

    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }
}
