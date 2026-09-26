using BatchPad.Core.Model;

namespace BatchPad.Core.Discovery;

/// <param name="RelativePath">Relative to the config file's folder, <c>/</c>-separated; the key explicit entries use.</param>
/// <param name="TreeFolders">Tree folders the script sits in: its sub-folders, or its prefix group.</param>
public sealed record DiscoveredScript(string RelativePath, string FullPath, IReadOnlyList<string> TreeFolders);

public static class ScriptFolderScanner
{
    public static readonly IReadOnlyList<ScriptFolder> DefaultFolders = [new ScriptFolder { Path = ".batchpad/scripts" }];
    public static readonly IReadOnlyList<string> DefaultInclude = ["*.bat", "*.cmd", "*.py", "*.ps1", "*.cs"];

    public static List<DiscoveredScript> Scan(string baseDirectory, IEnumerable<ScriptFolder> folders)
    {
        var found = new Dictionary<string, DiscoveredScript>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
            foreach (var script in ScanFolder(baseDirectory, folder))
                found.TryAdd(script.RelativePath, script);
        return [.. found.Values];
    }

    public static string FullPath(string baseDirectory, ScriptFolder folder) =>
        Path.GetFullPath(Path.Combine(baseDirectory, folder.Path));

    public static string RelativeKey(string baseDirectory, string fullPath) =>
        Path.GetRelativePath(baseDirectory, fullPath).Replace('\\', '/');

    private static List<DiscoveredScript> ScanFolder(string baseDirectory, ScriptFolder folder)
    {
        var root = FullPath(baseDirectory, folder);
        if (!Directory.Exists(root))
            return [];

        var scripts = new List<DiscoveredScript>();
        foreach (var file in EnumerateFiles(root, folder.Recurse ?? true))
        {
            var inFolder = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (!Picks(folder, inFolder))
                continue;
            var subFolders = inFolder.Split('/')[..^1];
            scripts.Add(new DiscoveredScript(RelativeKey(baseDirectory, file), file, subFolders));
        }
        return folder.GroupByPrefix ? GroupTopLevelByPrefix(scripts) : scripts;
    }

    /// <param name="inFolder">Relative to the folder, <c>/</c>-separated.</param>
    public static bool Picks(ScriptFolder folder, string inFolder)
    {
        var include = folder.Include is { Count: > 0 } ? folder.Include : DefaultInclude;
        return include.Any(p => Glob.IsMatch(p, inFolder)) && !(folder.Exclude ?? []).Any(p => Glob.IsMatch(p, inFolder));
    }

    private static IEnumerable<string> EnumerateFiles(string root, bool recurse)
    {
        foreach (var file in Directory.EnumerateFiles(root))
            yield return file;
        if (!recurse)
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            // .git, .vs and the like are never script folders.
            if (Path.GetFileName(dir).StartsWith('.'))
                continue;
            foreach (var file in EnumerateFiles(dir, recurse))
                yield return file;
        }
    }

    private static List<DiscoveredScript> GroupTopLevelByPrefix(List<DiscoveredScript> scripts)
    {
        var groups = scripts
            .Where(s => s.TreeFolders.Count == 0 && Prefix(s.FullPath) is not null)
            .GroupBy(s => Prefix(s.FullPath)!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToDictionary(g => g.Key, g => char.ToUpperInvariant(g.Key[0]) + g.Key[1..], StringComparer.OrdinalIgnoreCase);

        return [.. scripts.Select(s =>
            s.TreeFolders.Count == 0 && Prefix(s.FullPath) is { } prefix && groups.TryGetValue(prefix, out var group)
                ? s with { TreeFolders = [group] }
                : s)];
    }

    private static string? Prefix(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var underscore = name.IndexOf('_');
        return underscore > 0 ? name[..underscore] : null;
    }
}
