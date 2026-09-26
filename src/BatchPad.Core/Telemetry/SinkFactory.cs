namespace BatchPad.Core.Telemetry;

public static class SinkFactory
{
    public static ITelemetrySink Create(SinkConfig config) => Create(config, handler: null);

    /// <param name="handler">Carries the HTTP sinks' requests; tests pass a fake one.</param>
    public static ITelemetrySink Create(SinkConfig config, HttpMessageHandler? handler) => config.Type switch
    {
        SinkTypes.Jsonl => new JsonlSink(config),
        SinkTypes.Otlp => new OtlpSink(config, handler),
        SinkTypes.Elastic => new ElasticSink(config, handler),
        SinkTypes.Influx => new InfluxSink(config, handler),
        SinkTypes.Http => new HttpSink(config, handler),
        _ => throw new TelemetrySendException($"Unknown sink type '{config.Type}'.", retry: false),
    };
}
