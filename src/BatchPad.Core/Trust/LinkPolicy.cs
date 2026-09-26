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
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile && !IsDriveLetter(uri))
            return !SafeSchemes.Contains(uri.Scheme);

        var path = uri is { IsFile: true } ? uri.LocalPath : target;
        return ExecutableExtensions.Contains(Path.GetExtension(path.TrimEnd('/', '\\', ' ', '.')));
    }

    public static LinkAction Decide(string target, bool workspaceTrusted) =>
        !IsExecutable(target) ? LinkAction.Open
        : workspaceTrusted ? LinkAction.Confirm
        : LinkAction.Refuse;

    private static bool IsDriveLetter(Uri uri) => uri.Scheme.Length == 1;
}
