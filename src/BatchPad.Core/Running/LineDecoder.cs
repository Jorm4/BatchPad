using System.Buffers;
using System.Text;
using System.Text.Unicode;

namespace BatchPad.Core.Running;

/// <summary>Splits a byte stream into lines, each read as UTF-8 when valid and otherwise in the OEM code page (§4).</summary>
public static class LineDecoder
{
    private const int MaxLineBytes = 64 * 1024;

    private static readonly Lazy<Encoding> OemEncoding = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding((int)NativeMethods.GetOEMCP());
    });

    public static string Decode(ReadOnlySpan<byte> line) =>
        Utf8.IsValid(line) ? Encoding.UTF8.GetString(line) : OemEncoding.Value.GetString(line);

    public static void ReadLines(Stream stream, Action<string> onLine)
    {
        var pending = new ArrayBufferWriter<byte>();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            var chunk = buffer.AsSpan(0, read);
            int newline;
            while ((newline = chunk.IndexOf((byte)'\n')) >= 0)
            {
                pending.Write(chunk[..newline]);
                Emit(pending, onLine);
                chunk = chunk[(newline + 1)..];
            }
            pending.Write(chunk);
            if (pending.WrittenCount >= MaxLineBytes)
                Emit(pending, onLine);
        }
        if (pending.WrittenCount > 0)
            Emit(pending, onLine);
    }

    private static void Emit(ArrayBufferWriter<byte> pending, Action<string> onLine)
    {
        var line = pending.WrittenSpan;
        if (line.Length > 0 && line[^1] == '\r')
            line = line[..^1];
        onLine(Decode(line));
        pending.ResetWrittenCount();
    }
}
