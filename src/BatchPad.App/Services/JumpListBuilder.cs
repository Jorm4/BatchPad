using BatchPad.App.ViewModels;
using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.App.Services;

/// <param name="Category">Null for the jump list's Tasks section.</param>
public sealed record JumpListItem(string Title, string Arguments, string? Description = null, string? Category = null);

public static class JumpListBuilder
{
    public const string RecentCategory = "Recent workspaces";

    /// <summary>A task per pinned My Scripts entry of the open workspace, then the recent workspaces.</summary>
    public static IReadOnlyList<JumpListItem> Build(TreeViewModel? tree, string? workspaceFile, IEnumerable<string> recentWorkspaces)
    {
        var pinned = tree is null || workspaceFile is null
            ? []
            : tree.MyScriptsRoot.Descendants()
                .Where(n => n is { IsMyScript: true, IsBroken: false, Node: RunnableNode { Pinned: true } })
                .Select(n => new JumpListItem(n.Name, ArgvQuoter.Join(["--run", n.Key, "--workspace", workspaceFile]), n.Description));
        var recents = recentWorkspaces.Select(file => new JumpListItem(new WorkspaceChoice(file).DisplayName, ArgvQuoter.Quote(file), file, RecentCategory));
        return [.. pinned, .. recents];
    }
}
