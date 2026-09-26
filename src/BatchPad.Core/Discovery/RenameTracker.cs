using BatchPad.Core.Config;
using BatchPad.Core.Detection;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Discovery;

public sealed record FileRename(string OldFullPath, string NewFullPath);

/// <summary>
/// Collects renames seen by a <see cref="FolderWatcher"/> and carries entries' <c>path</c>, <c>seenPaths</c> and
/// dismissed proposals along to the new name (§3.10).
/// </summary>
public sealed class RenameTracker
{
    private readonly Lock _gate = new();
    private readonly List<FileRename> _pending = [];

    public void Observe(FileChange change)
    {
        if (change is { Kind: WatcherChangeTypes.Renamed, OldFullPath: { } old })
            Add(new FileRename(Path.GetFullPath(old), Path.GetFullPath(change.FullPath)));
    }

    public void Add(FileRename rename)
    {
        lock (_gate)
            if (!_pending.Contains(rename))
                _pending.Add(rename);
    }

    /// <summary>
    /// Applies the collected renames. A path counts as moved only when it is gone and its final name exists, so an editor
    /// that renames a file to a backup and writes a new one in its place moves nothing.
    /// </summary>
    /// <returns>True when a config file was written.</returns>
    public bool Apply(LoadedWorkspace workspace)
    {
        List<FileRename> renames;
        lock (_gate)
        {
            renames = [.. _pending];
            _pending.Clear();
        }
        return renames.Count > 0 && Move(workspace, renames, requireOnDisk: true);
    }

    public static bool Move(LoadedWorkspace workspace, IReadOnlyList<FileRename> renames, bool requireOnDisk = false)
    {
        string? MovedTo(string fullPath) =>
            Target(fullPath, renames) is { } target && (!requireOnDisk || (!Exists(fullPath) && Exists(target))) ? target : null;

        var written = false;
        foreach (var tree in workspace.AllTrees.Where(t => File.Exists(t.FilePath)))
        {
            if (!Retarget(tree.File.Scripts, tree.BaseDirectory, MovedTo, dryRun: true))
                continue;
            ConfigWriter.Update(tree.FilePath, file => Retarget(file.Scripts, tree.BaseDirectory, MovedTo, dryRun: false));
            written = true;
        }

        var userFile = workspace.MyScripts.FilePath;
        var user = File.Exists(userFile) ? ConfigReader.ReadFile(userFile) : null;
        if (user is not null && RetargetUserState(user, workspace, MovedTo))
        {
            ConfigWriter.Update(userFile, file => RetargetUserState(file, workspace, MovedTo));
            written = true;
        }
        return written;
    }

    /// <summary>Where <paramref name="fullPath"/> ends up after the renames, applied in order; null when none touch it.</summary>
    public static string? Target(string fullPath, IEnumerable<FileRename> renames)
    {
        var current = fullPath;
        foreach (var rename in renames)
        {
            if (string.Equals(current, rename.OldFullPath, StringComparison.OrdinalIgnoreCase))
                current = rename.NewFullPath;
            else if (current.StartsWith(rename.OldFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                current = rename.NewFullPath + current[rename.OldFullPath.Length..];
        }
        return string.Equals(current, fullPath, StringComparison.OrdinalIgnoreCase) ? null : current;
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static bool Retarget(List<TreeNode> nodes, string baseDirectory, Func<string, string?> movedTo, bool dryRun)
    {
        var any = false;
        foreach (var script in nodes.Descendants().OfType<ScriptNode>())
        {
            if (script.Path is { } path && !path.Contains("${") && movedTo(Path.GetFullPath(path, baseDirectory)) is { } target)
            {
                any = true;
                if (!dryRun)
                    script.Path = Path.IsPathRooted(path) ? target : ScriptFolderScanner.RelativeKey(baseDirectory, target);
            }
        }
        return any;
    }

    private static bool RetargetUserState(WorkspaceFile user, LoadedWorkspace workspace, Func<string, string?> movedTo)
    {
        var any = false;
        var baseDirectory = workspace.Workspace.BaseDirectory;
        if (user.SeenPaths is { } seen)
        {
            for (var i = 0; i < seen.Count; i++)
            {
                if (movedTo(Path.GetFullPath(seen[i], baseDirectory)) is { } target)
                {
                    seen[i] = ScriptFolderScanner.RelativeKey(baseDirectory, target);
                    any = true;
                }
            }
        }
        if (user.DismissedProposals is { } dismissed)
        {
            foreach (var key in dismissed.Keys.ToList())
            {
                if (NewProposalKey(key, workspace, movedTo) is { } newKey)
                {
                    dismissed[newKey] = dismissed[key];
                    dismissed.Remove(key);
                    any = true;
                }
            }
        }
        return any;
    }

    private static string? NewProposalKey(string key, LoadedWorkspace workspace, Func<string, string?> movedTo)
    {
        foreach (var tree in workspace.AllTrees.Where(t => t.Kind != TreeKind.MyScripts))
        {
            var prefix = ProposalTracker.ScriptKey(tree.Kind, "");
            if (!key.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            if (movedTo(Path.GetFullPath(key[prefix.Length..], tree.BaseDirectory)) is { } target)
                return ProposalTracker.ScriptKey(tree.Kind, Path.GetRelativePath(tree.BaseDirectory, target));
        }
        return null;
    }
}
