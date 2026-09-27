using BatchPad.Core.Workspace;

namespace BatchPad.Core.Trust;

/// <summary>Workspace folders the user trusts (§4.3), kept in settings. Trusting a folder trusts everything under it.</summary>
public sealed class TrustStore(Settings settings, string settingsFile)
{
    private readonly Lock _lock = new();
    private string[] _normalizedFrom = [];
    private string[] _normalized = [];

    public static TrustStore Load(AppPaths paths) => new(Settings.Load(paths.SettingsFile), paths.SettingsFile);

    public IReadOnlyList<string> TrustedFolders => settings.TrustedFolders;

    /// <summary>A trusted folder or one under it, or the same place in a verified worktree of a trusted repository (§4.5).</summary>
    public bool IsTrusted(string folder)
    {
        var candidate = PathIdentity.Normalize(folder);
        return IsUnderTrustedFolder(candidate)
            || Checkout.Locate(candidate) is { Kind: CheckoutKind.Worktree } worktree
            && IsUnderTrustedFolder(Path.Combine(worktree.Repository, Path.GetRelativePath(worktree.Directory, candidate)));
    }

    private bool IsUnderTrustedFolder(string candidate) =>
        NormalizedTrustedFolders().Any(trusted =>
            candidate.Equals(trusted, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(trusted.EndsWith(Path.DirectorySeparatorChar) ? trusted : trusted + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));

    public void Trust(string folder)
    {
        if (IsTrusted(folder))
            return;
        var normalized = PathIdentity.Normalize(folder);
        settings.Update(settingsFile, s =>
        {
            if (!s.TrustedFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                s.TrustedFolders.Add(normalized);
        });
    }

    /// <returns>Whether <paramref name="folder"/> itself was trusted; one trusted through a folder above it stays trusted.</returns>
    public bool Revoke(string folder)
    {
        var normalized = PathIdentity.Normalize(folder);
        bool Matches(string trusted) => normalized.Equals(NormalizeEntry(trusted), StringComparison.OrdinalIgnoreCase);
        if (!settings.TrustedFolders.Any(Matches))
            return false;
        settings.Update(settingsFile, s => s.TrustedFolders.RemoveAll(Matches));
        return true;
    }

    private string[] NormalizedTrustedFolders()
    {
        lock (_lock)
        {
            if (!settings.TrustedFolders.SequenceEqual(_normalizedFrom))
            {
                _normalizedFrom = [.. settings.TrustedFolders];
                _normalized = [.. _normalizedFrom.Select(NormalizeEntry).OfType<string>()];
            }
            return _normalized;
        }
    }

    /// <summary>A hand-edited entry that is blank or relative would otherwise trust the current folder.</summary>
    private static string? NormalizeEntry(string? trusted)
    {
        if (string.IsNullOrWhiteSpace(trusted) || !Path.IsPathFullyQualified(trusted))
            return null;
        try
        {
            return PathIdentity.Normalize(trusted);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
