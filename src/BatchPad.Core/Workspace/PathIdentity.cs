using BatchPad.Core.Running;

namespace BatchPad.Core.Workspace;

/// <summary>Full, long-name paths, so git's own absolute paths compare equal to ours even under an 8.3 temp folder.</summary>
internal static class PathIdentity
{
    public static string Normalize(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return full.Contains('~') && OperatingSystem.IsWindows() ? LongForm(full) : full;
    }

    public static bool Same(string first, string second) =>
        string.Equals(Normalize(first), Normalize(second), StringComparison.OrdinalIgnoreCase);

    private static string LongForm(string path)
    {
        var needed = TryLongForm(path, stackalloc char[260], out var result);
        if (result is null && needed is > 0 and <= (uint)short.MaxValue)
            TryLongForm(path, new char[needed], out result);
        return result ?? path;
    }

    /// <returns>The buffer length needed, when <paramref name="buffer"/> was too small.</returns>
    private static unsafe uint TryLongForm(string path, Span<char> buffer, out string? result)
    {
        fixed (char* start = buffer)
        {
            var length = NativeMethods.GetLongPathName(path, start, (uint)buffer.Length);
            result = length > 0 && length < buffer.Length ? new string(start, 0, (int)length) : null;
            return length;
        }
    }
}
