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

    private static unsafe string LongForm(string path)
    {
        var buffer = new char[short.MaxValue];
        fixed (char* start = buffer)
        {
            var length = NativeMethods.GetLongPathName(path, start, (uint)buffer.Length);
            return length > 0 && length < buffer.Length ? new string(start, 0, (int)length) : path;
        }
    }
}
