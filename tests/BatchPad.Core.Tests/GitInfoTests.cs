using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class GitInfoTests
{
    private const string Commit = "3f9a1c2b4d5e6f708192a3b4c5d6e7f809102132";
    private const string Other = "0123456789abcdef0123456789abcdef01234567";

    [TestMethod]
    public void ABranchIsReadFromHeadAndItsLooseRefFromASubfolder()
    {
        using var dir = new TempDir();
        Write(dir.Path(".git", "HEAD"), "ref: refs/heads/feature/x\n");
        Write(dir.Path(".git", "refs", "heads", "feature", "x"), Commit + "\n");
        Directory.CreateDirectory(dir.Path("tools", "deep"));

        Assert.AreEqual(new GitInfo("feature/x", Commit), GitInfo.Read(dir.Path("tools", "deep")));
    }

    [TestMethod]
    public void ADetachedHeadHasACommitAndNoBranch()
    {
        using var dir = new TempDir();
        Write(dir.Path(".git", "HEAD"), Commit + "\n");

        Assert.AreEqual(new GitInfo(null, Commit), GitInfo.Read(dir.Root));
    }

    [TestMethod]
    public void APackedRefIsFound()
    {
        using var dir = new TempDir();
        Write(dir.Path(".git", "HEAD"), "ref: refs/heads/main\n");
        Write(dir.Path(".git", "packed-refs"), $"# pack-refs with: peeled fully-peeled sorted\n{Other} refs/heads/maintenance\n{Commit} refs/heads/main\n^{Other}\n");

        Assert.AreEqual(new GitInfo("main", Commit), GitInfo.Read(dir.Root));
    }

    [TestMethod]
    public void ALinkedWorktreeReadsItsOwnHeadAndTheMainRepositorysRefs()
    {
        using var dir = new TempDir();
        Write(dir.Path("main", ".git", "HEAD"), "ref: refs/heads/main\n");
        Write(dir.Path("main", ".git", "refs", "heads", "topic"), Commit + "\n");
        Write(dir.Path("main", ".git", "worktrees", "wt", "HEAD"), "ref: refs/heads/topic\n");
        Write(dir.Path("main", ".git", "worktrees", "wt", "commondir"), "../..\n");
        Write(dir.Path("wt", ".git"), "gitdir: ../main/.git/worktrees/wt\n");

        Assert.AreEqual(new GitInfo("topic", Commit), GitInfo.Read(dir.Path("wt")));
    }

    [TestMethod]
    public void AFolderOutsideAnyRepositoryHasNone()
    {
        using var dir = new TempDir();

        Assert.IsNull(GitInfo.Read(dir.Root));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
