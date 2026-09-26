namespace BatchPad.Core.Telemetry;

/// <summary>A forwarding target. Any exception but a non-retryable <see cref="TelemetrySendException"/> means "try again later".</summary>
public interface ITelemetrySink
{
    Task SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken);
}

/// <param name="retry">False when resending can't help (a 4xx answer, a bad setting): the batch is dropped.</param>
public sealed class TelemetrySendException(string message, bool retry = true, Exception? inner = null) : Exception(message, inner)
{
    public bool Retry { get; } = retry;
}
