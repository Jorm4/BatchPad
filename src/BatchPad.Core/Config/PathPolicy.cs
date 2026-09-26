namespace BatchPad.Core.Config;

public sealed class UnsafePathException(string message) : Exception(message);

/// <summary>
/// Which paths a config file may reach (§4.3). A workspace's files stay inside its folder until the workspace is trusted,
/// and never reach network shares outside it or devices, which could leak credentials or hang a read.
/// </summary>
public sealed class PathPolicy
{
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    private readonly string? root;
    private readonly bool trusted;

    private PathPolicy(string? root, bool trusted)
    {
        this.root = root;
        this.trusted = trusted;
    }

    /// <summary>For the user's own files: <c>global.json</c> libraries may well live on a share.</summary>
    public static PathPolicy Unrestricted { get; } = new(null, true);

    public static PathPolicy Workspace(string root, bool trusted) =>
        new(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), trusted);

    public string Resolve(string path, string baseDirectory)
    {
        var fullPath = Path.GetFullPath(path, baseDirectory);
        return Problem(fullPath) is { } problem ? throw new UnsafePathException(problem) : fullPath;
    }

    /// <summary>Why the policy refuses <paramref name="fullPath"/>; null when it allows it.</summary>
    public string? Problem(string fullPath)
    {
        if (root is null)
            return null;
        if (IsDevicePath(fullPath))
            return $"'{fullPath}' is a device path, which a workspace file may not use.";
        if (IsInside(fullPath))
            return null;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal) || fullPath.StartsWith("//", StringComparison.Ordinal))
            return $"'{fullPath}' is a network path outside the workspace folder, which a workspace file may not use.";
        return trusted ? null : $"'{fullPath}' is outside the workspace folder; trust the workspace to use it.";
    }

    /// <summary>Reserved names such as <c>CON</c> or <c>nul.txt</c>, which Windows opens as devices.</summary>
    public static bool IsDeviceName(string name) => DeviceNames.Contains(name.Split('.')[0].TrimEnd(' '));

    private static bool IsDevicePath(string fullPath) =>
        fullPath.Replace('/', '\\') is var path
        && (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal)
            || path.Split('\\', StringSplitOptions.RemoveEmptyEntries).Any(IsDeviceName));

    private bool IsInside(string fullPath)
    {
        var prefix = root!.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
