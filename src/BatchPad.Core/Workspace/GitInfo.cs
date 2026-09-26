namespace BatchPad.Core.Workspace;

/// <summary>The checked-out branch and commit of a git working tree, read from its files without running git.</summary>
/// <param name="Branch">Null when HEAD is detached.</param>
public sealed record GitInfo(string? Branch, string? Commit)
{
    private const string RefPrefix = "ref:";
    private const string BranchPrefix = "refs/heads/";

    /// <summary>The working tree containing <paramref name="directory"/>, or null when it isn't in one or can't be read.</summary>
    public static GitInfo? Read(string directory)
    {
        try
        {
            return FindGitDirectory(directory) is { } gitDirectory ? FromGitDirectory(gitDirectory) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? FindGitDirectory(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            var dotGit = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(dotGit))
                return dotGit;
            if (File.Exists(dotGit))
                return GitDirectoryNamedBy(dotGit);
        }
        return null;
    }

    internal static string? GitDirectoryNamedBy(string dotGitFile) =>
        File.ReadLines(dotGitFile).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal)) is { } line
            ? Path.GetFullPath(line["gitdir:".Length..].Trim(), Path.GetDirectoryName(dotGitFile)!)
            : null;

    internal static GitInfo? FromGitDirectory(string gitDirectory)
    {
        var headPath = Path.Combine(gitDirectory, "HEAD");
        if (!File.Exists(headPath))
            return null;
        var head = File.ReadAllText(headPath).Trim();
        if (!head.StartsWith(RefPrefix, StringComparison.Ordinal))
            return new GitInfo(null, head.Length > 0 ? head : null);

        var reference = head[RefPrefix.Length..].Trim();
        var branch = reference.StartsWith(BranchPrefix, StringComparison.Ordinal) ? reference[BranchPrefix.Length..] : reference;
        return new GitInfo(branch, ResolveRef(gitDirectory, reference) ?? ResolveRef(CommonDirectory(gitDirectory), reference));
    }

    // A linked worktree keeps HEAD in its own directory but refs in the main repository's.
    internal static string CommonDirectory(string gitDirectory)
    {
        var file = Path.Combine(gitDirectory, "commondir");
        return File.Exists(file) ? Path.GetFullPath(File.ReadAllText(file).Trim(), gitDirectory) : gitDirectory;
    }

    private static string? ResolveRef(string gitDirectory, string reference)
    {
        var loose = Path.Combine(gitDirectory, reference.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(loose) && File.ReadAllText(loose).Trim() is { Length: > 0 } commit)
            return commit;
        var packed = Path.Combine(gitDirectory, "packed-refs");
        if (!File.Exists(packed))
            return null;
        foreach (var line in File.ReadLines(packed))
        {
            var space = line.IndexOf(' ');
            if (space > 0 && line[0] is not ('#' or '^') && line.AsSpan(space + 1).Trim().SequenceEqual(reference))
                return line[..space];
        }
        return null;
    }
}
