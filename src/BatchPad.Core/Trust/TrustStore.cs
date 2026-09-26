using BatchPad.Core.Workspace;

namespace BatchPad.Core.Trust;

/// <summary>Workspace folders the user trusts (§4.3), kept in settings. Trusting a folder trusts everything under it.</summary>
public sealed class TrustStore(Settings settings, string settingsFile)
{
    public static TrustStore Load(AppPaths paths) => new(Settings.Load(paths.SettingsFile), paths.SettingsFile);

    public IReadOnlyList<string> TrustedFolders => settings.TrustedFolders;

    public bool IsTrusted(string folder)
    {
        var candidate = Normalize(folder);
        return settings.TrustedFolders.Select(Normalize).Any(trusted =>
            candidate.Equals(trusted, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(trusted.EndsWith(Path.DirectorySeparatorChar) ? trusted : trusted + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    public void Trust(string folder)
    {
        if (IsTrusted(folder))
            return;
        settings.TrustedFolders.Add(Normalize(folder));
        settings.Save(settingsFile);
    }

    public void Revoke(string folder)
    {
        var normalized = Normalize(folder);
        if (settings.TrustedFolders.RemoveAll(f => Normalize(f).Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0)
            settings.Save(settingsFile);
    }

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
