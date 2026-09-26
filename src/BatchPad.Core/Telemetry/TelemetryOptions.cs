using BatchPad.Core.Model;

namespace BatchPad.Core.Telemetry;

/// <summary>The <c>telemetry</c> block of settings.json (§4.4); workspace files can't carry it.</summary>
public sealed class TelemetryOptions : ExtensibleObject
{
    public bool Machine { get; set; } = true;
    public bool User { get; set; }
    public bool IncludeValues { get; set; }
    public bool HashNames { get; set; }

    /// <summary>Never null and never holding null, whatever settings.json says.</summary>
    public List<SinkConfig> Sinks { get; set => field = value is null ? [] : [.. value.OfType<SinkConfig>()]; } = [];
}
