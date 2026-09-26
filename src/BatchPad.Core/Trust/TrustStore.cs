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

    public bool IsTrusted(string folder)
    {
        var candidate = Normalize(folder);
        return NormalizedTrustedFolders().Any(trusted =>
            candidate.Equals(trusted, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(trusted.EndsWith(Path.DirectorySeparatorChar) ? trusted : trusted + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }

    public void Trust(string folder)
    {
        if (IsTrusted(folder))
            return;
        var normalized = Normalize(folder);
        settings.Update(settingsFile, s =>
        {
            if (!s.TrustedFolders.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                s.TrustedFolders.Add(normalized);
        });
    }

    public void Revoke(string folder)
    {
        var normalized = Normalize(folder);
        bool Matches(string trusted) => Normalize(trusted).Equals(normalized, StringComparison.OrdinalIgnoreCase);
        if (settings.TrustedFolders.Any(Matches))
            settings.Update(settingsFile, s => s.TrustedFolders.RemoveAll(Matches));
    }

    private string[] NormalizedTrustedFolders()
    {
        lock (_lock)
        {
            if (!settings.TrustedFolders.SequenceEqual(_normalizedFrom))
            {
                _normalizedFrom = [.. settings.TrustedFolders];
                _normalized = [.. _normalizedFrom.Select(Normalize)];
            }
            return _normalized;
        }
    }

    private static string Normalize(string folder) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
}
