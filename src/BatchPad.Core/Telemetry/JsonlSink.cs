using System.Text;

namespace BatchPad.Core.Telemetry;

/// <summary>Appends one event per line to a file; past <c>maxSizeMb</c> the file becomes <c>&lt;name&gt;.1.jsonl</c> and a new one starts.</summary>
public sealed class JsonlSink(SinkConfig config) : ITelemetrySink
{
    public async Task SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken)
    {
        var resolved = config.Resolve();
        if (string.IsNullOrWhiteSpace(resolved.Path))
            throw new TelemetrySendException("The jsonl sink has no path.", retry: false);
        var path = Path.GetFullPath(resolved.Path);
        var text = new StringBuilder();
        foreach (var telemetryEvent in batch)
            text.Append(telemetryEvent.ToJson()).Append('\n');
        var bytes = Encoding.UTF8.GetBytes(text.ToString());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var limit = (long)(resolved.MaxSizeMb ?? SinkConfig.DefaultMaxSizeMb) * 1024 * 1024;
        var file = new FileInfo(path);
        if (file.Exists && file.Length > 0 && file.Length + bytes.Length > limit)
            File.Move(path, Path.ChangeExtension(path, ".1" + Path.GetExtension(path)), overwrite: true);
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await stream.WriteAsync(bytes, cancellationToken);
    }
}
