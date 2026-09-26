using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;

namespace BatchPad.Core.Workspace;

/// <summary>
/// Loads and saves a workspace's <c>user.json</c> (§3.7). Every save re-reads the file under a lock, so two windows
/// saving different entries keep both.
/// </summary>
public sealed class UserStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public static UserStore For(LoadedWorkspace workspace) => new(workspace.MyScripts.FilePath);

    public WorkspaceFile Load() => File.Exists(FilePath) ? ConfigReader.ReadFile(FilePath) : new WorkspaceFile();

    public WorkspaceFile Update(Action<WorkspaceFile> change) => ConfigWriter.Update(FilePath, change);

    /// <summary>Adds <paramref name="entry"/> at the top level or into the named folder (created if missing), giving it an id from its name when it has none.</summary>
    public string Add(RunnableNode entry, string? folder = null)
    {
        var id = entry.Id;
        Update(file =>
        {
            id ??= IdAssigner.FromName(entry.Name ?? "my-script", Ids(file.Scripts));
            entry.Id = id;
            ItemsOf(file, folder).Add(entry);
        });
        return id!;
    }

    /// <summary>Replaces the entry matching <paramref name="original"/>: the same id, or for an entry without one, the same content.</summary>
    /// <returns>False when no entry matches any more.</returns>
    public bool Replace(TreeNode original, TreeNode replacement)
    {
        var replaced = false;
        Update(file => replaced = Edit(file.Scripts, original, (items, index) => items[index] = replacement));
        return replaced;
    }

    public bool Remove(TreeNode original)
    {
        var removed = false;
        Update(file => removed = Edit(file.Scripts, original, (items, index) => items.RemoveAt(index)));
        return removed;
    }

    /// <summary>Records discovered script paths as seen, so they no longer show as New (§3.10).</summary>
    public void MarkSeen(IEnumerable<string> paths)
    {
        var added = paths.ToList();
        Update(file =>
        {
            var seen = file.SeenPaths ??= [];
            var known = new HashSet<string>(seen, StringComparer.OrdinalIgnoreCase);
            seen.AddRange(added.Where(known.Add));
        });
    }

    public void DismissProposal(string scriptKey, string proposalKey) =>
        Update(file =>
        {
            var all = file.DismissedProposals ??= [];
            if (!all.TryGetValue(scriptKey, out var dismissed))
                all[scriptKey] = dismissed = [];
            if (!dismissed.Contains(proposalKey))
                dismissed.Add(proposalKey);
        });

    private static List<TreeNode> ItemsOf(WorkspaceFile file, string? folder)
    {
        if (folder is null)
            return file.Scripts;
        if (file.Scripts.OfType<FolderNode>().FirstOrDefault(f => f.Folder == folder) is { } existing)
            return existing.Items;
        var created = new FolderNode { Folder = folder };
        file.Scripts.Add(created);
        return created.Items;
    }

    private static bool Edit(List<TreeNode> items, TreeNode original, Action<List<TreeNode>, int> edit)
    {
        var originalJson = ReferenceResolver.IdOf(original) is null ? Json(original) : null;
        for (var i = 0; i < items.Count; i++)
        {
            if (Matches(items[i], original, originalJson))
            {
                edit(items, i);
                return true;
            }
            if (items[i] is FolderNode folder && Edit(folder.Items, original, edit))
                return true;
        }
        return false;
    }

    private static bool Matches(TreeNode candidate, TreeNode original, string? originalJson) =>
        originalJson is null
            ? ReferenceResolver.IdOf(candidate) == ReferenceResolver.IdOf(original)
            : candidate.GetType() == original.GetType() && Json(candidate) == originalJson;

    private static string Json(TreeNode node) => ConfigJson.Serialize(node);

    private static HashSet<string> Ids(IEnumerable<TreeNode> nodes) =>
        nodes.Descendants().Select(ReferenceResolver.IdOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
}
