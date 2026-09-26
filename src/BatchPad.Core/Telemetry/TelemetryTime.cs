using System.Globalization;

namespace BatchPad.Core.Telemetry;

internal static class TelemetryTime
{
    public static string UtcMilliseconds(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static long UnixNanoseconds(DateTimeOffset time) => (time.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;
}
