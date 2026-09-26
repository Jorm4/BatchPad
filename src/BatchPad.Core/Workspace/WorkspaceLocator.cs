namespace BatchPad.Core.Workspace;

public static class WorkspaceLocator
{
    public const string FileName = "batchpad.json";

    /// <summary>
    /// The command-line path (a file or a folder) wins even when missing, so a typo is reported instead of silently
    /// opening something else; then the nearest <c>batchpad.json</c> upwards; then the first recent that still exists.
    /// </summary>
    public static string? Locate(string? commandLinePath, string currentDirectory, IEnumerable<string> recentWorkspaces)
    {
        if (!string.IsNullOrWhiteSpace(commandLinePath))
        {
            var fullPath = Path.GetFullPath(commandLinePath, currentDirectory);
            return Directory.Exists(fullPath) ? Path.Combine(fullPath, FileName) : fullPath;
        }
        return FindUpwards(currentDirectory) ?? recentWorkspaces.FirstOrDefault(File.Exists);
    }

    /// <summary>The nearest <c>batchpad.json</c> upwards, never above a linked worktree's root: a worktree without one must not use the main checkout's.</summary>
    public static string? FindUpwards(string startDirectory)
    {
        var start = Path.GetFullPath(startDirectory);
        var worktreeRoot = Checkout.Read(start) is { Kind: CheckoutKind.Worktree } worktree ? worktree.Directory : null;
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate))
                return candidate;
            if (worktreeRoot is not null && PathIdentity.Same(dir.FullName, worktreeRoot))
                break;
        }
        return null;
    }
}
