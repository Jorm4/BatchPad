using BatchPad.Core.Running;

namespace BatchPad.Core.Trust;

public enum LinkAction { Open, Confirm, Refuse }

/// <summary>Decides how a link may open (§4.3): executable targets need confirmation, and never open when untrusted.</summary>
public static class LinkPolicy
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bat", ".cmd", ".exe", ".com", ".lnk", ".ps1", ".psm1", ".psd1", ".vbs", ".vbe", ".js", ".jse", ".wsf",
        ".wsh", ".msi", ".msp", ".msc", ".scr", ".pif", ".hta", ".cpl", ".reg", ".py", ".pyw", ".cs", ".csx",
        ".jar", ".appref-ms", ".application", ".url", ".scf", ".inf", ".settingcontent-ms",
    };

    private static readonly HashSet<string> SafeSchemes = new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto" };

    /// <summary>Executable files, and URLs with protocol handlers other than web and mail.</summary>
    public static bool IsExecutable(string target)
    {
        if (AsUrl(target) is { } url)
            return !SafeSchemes.Contains(url.Scheme);
        var extension = Path.GetExtension(LocalPath(target).TrimEnd('/', '\\', ' ', '.'));
        return extension.Length > 0 && (ExecutableExtensions.Contains(extension) || NativeMethods.AssocIsDangerous(extension));
    }

    public static LinkAction Decide(string target, bool workspaceTrusted) =>
        !IsExecutable(target) ? LinkAction.Open
        : workspaceTrusted ? LinkAction.Confirm
        : LinkAction.Refuse;

    /// <summary>
    /// Whether a ready URL or artifact may open with nobody asked: never an executable, and never a network or device path,
    /// which Windows would contact just to look at.
    /// </summary>
    public static bool OpensUnasked(string target) => !IsExecutable(target) && !IsNetworkOrDevicePath(LocalPath(target));

    /// <summary>Whether the shell may open <paramref name="target"/>: web and mail links, or existing files and folders; executables only once confirmed.</summary>
    public static bool MayOpen(string target, bool confirmed)
    {
        if (AsUrl(target) is { } url)
            return confirmed || SafeSchemes.Contains(url.Scheme);
        var path = LocalPath(target);
        return (confirmed || !IsExecutable(path)) && (File.Exists(path) || Directory.Exists(path));
    }

    /// <summary>The target as a URL other than a file path; null for a path.</summary>
    public static Uri? AsUrl(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Scheme.Length > 1 ? uri : null;

    private static string LocalPath(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : target;

    internal static bool IsNetworkOrDevicePath(string path) =>
        path.Length >= 2 && path[0] is '\\' or '/' && path[1] is '\\' or '/';
}
