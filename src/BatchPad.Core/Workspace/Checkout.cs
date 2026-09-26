namespace BatchPad.Core.Workspace;

public enum CheckoutKind { Main, Worktree }

/// <summary>The git checkout a folder is in (§4.5): the main working tree, or a linked worktree verified in both directions.</summary>
/// <param name="Name">The worktree's folder name, or <see cref="MainName"/>.</param>
/// <param name="Repository">The main working tree's folder; a <c>.git</c> file that doesn't verify counts as its own repository.</param>
public sealed record Checkout(string Directory, CheckoutKind Kind, string Name, string? Branch, string? Commit, string Repository)
{
    public const string MainName = "main";

    public string Describe() => Branch is { } branch ? $"{Name} ({branch})" : Name;

    /// <summary>The folder of the checkout containing <paramref name="directory"/>, else the directory itself.</summary>
    public static string DirectoryOf(string directory) => Locate(directory)?.Directory ?? PathIdentity.Normalize(directory);

    /// <summary>The checkout containing <paramref name="directory"/>, or null when it isn't in one or can't be read.</summary>
    public static Checkout? Read(string directory)
    {
        if (Locate(directory) is not { } located)
            return null;
        var git = located.GitDirectory is null ? null : GitInfo.FromGitDirectory(located.GitDirectory);
        return new Checkout(located.Directory, located.Kind, located.Name, git?.Branch, git?.Commit, located.Repository);
    }

    /// <summary>Where the checkout containing <paramref name="directory"/> is, without reading its branch or commit.</summary>
    internal static Located? Locate(string directory)
    {
        try
        {
            for (var current = new DirectoryInfo(PathIdentity.Normalize(directory)); current is not null; current = current.Parent)
            {
                var dotGit = Path.Combine(current.FullName, ".git");
                if (System.IO.Directory.Exists(dotGit))
                    return new Located(current.FullName, CheckoutKind.Main, MainName, dotGit, current.FullName);
                if (File.Exists(dotGit))
                    return FromGitFile(current.FullName, dotGit);
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Located FromGitFile(string directory, string dotGit)
    {
        var gitDirectory = GitInfo.GitDirectoryNamedBy(dotGit);
        return gitDirectory is not null && VerifiedRepository(dotGit, gitDirectory) is { } repository
            ? new Located(directory, CheckoutKind.Worktree, Path.GetFileName(directory), gitDirectory, repository)
            : new Located(directory, CheckoutKind.Main, MainName, gitDirectory, directory);
    }

    /// <summary>
    /// The repository <paramref name="gitDirectory"/> belongs to, when it is that repository's <c>worktrees/&lt;name&gt;</c>
    /// and its <c>gitdir</c> file points back at <paramref name="dotGit"/>; a <c>.git</c> file alone proves nothing.
    /// </summary>
    private static string? VerifiedRepository(string dotGit, string gitDirectory)
    {
        var backPointer = Path.Combine(gitDirectory, "gitdir");
        var commonFile = Path.Combine(gitDirectory, "commondir");
        if (!File.Exists(backPointer) || !File.Exists(commonFile))
            return null;
        if (GitInfo.LocalPathNamedIn(backPointer, File.ReadAllText(backPointer).Trim()) is not { } pointedAt || !PathIdentity.Same(pointedAt, dotGit))
            return null;
        if (GitInfo.LocalPathNamedIn(commonFile, File.ReadAllText(commonFile).Trim()) is not { } commonDirectory)
            return null;
        var common = PathIdentity.Normalize(commonDirectory);
        if (!string.Equals(Path.GetDirectoryName(PathIdentity.Normalize(gitDirectory)), Path.Combine(common, "worktrees"), StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetFileName(common).Equals(".git", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(common)! : common;
    }

    /// <param name="GitDirectory">Null when the <c>.git</c> file names no directory BatchPad will read.</param>
    internal sealed record Located(string Directory, CheckoutKind Kind, string Name, string? GitDirectory, string Repository);
}
