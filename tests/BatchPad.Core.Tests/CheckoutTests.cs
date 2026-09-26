using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class CheckoutTests
{
    private static GitRepo s_repo = null!;

    [ClassInitialize]
    public static void CreateRepository(TestContext _) => s_repo = new GitRepo(new Dictionary<string, string>
    {
        ["batchpad.json"] = """{ "scripts": [] }""",
        ["tools/readme.txt"] = "tools",
    });

    [ClassCleanup]
    public static void RemoveRepository() => s_repo.Dispose();

    [TestMethod]
    public void AWorktreeIsReportedWithItsNameAndBranchAndTheMainCheckoutAsMain()
    {
        var worktree = Checkout.Read(Path.Combine(s_repo.Worktree, "tools"))!;
        var main = Checkout.Read(s_repo.Main)!;

        Assert.AreEqual(CheckoutKind.Worktree, worktree.Kind);
        Assert.AreEqual("wt", worktree.Name);
        Assert.AreEqual(GitRepo.WorktreeBranch, worktree.Branch);
        Assert.IsTrue(PathIdentity.Same(s_repo.Worktree, worktree.Directory));
        Assert.IsTrue(PathIdentity.Same(s_repo.Main, worktree.Repository));
        Assert.AreEqual(GitRepo.Git(s_repo.Main, "rev-parse", "HEAD").Trim(), worktree.Commit);

        Assert.AreEqual(CheckoutKind.Main, main.Kind);
        Assert.AreEqual(Checkout.MainName, main.Name);
        Assert.AreEqual("main", main.Branch);
        Assert.AreEqual(worktree.Commit, main.Commit);
        Assert.IsTrue(PathIdentity.Same(s_repo.Main, main.Repository));
    }

    [TestMethod]
    public void AWorktreeOfATrustedRepositoryIsTrustedAndOfAnUntrustedOneIsNot()
    {
        using var dir = new TempDir();

        Assert.IsTrue(TrustIn(dir, s_repo.Main).IsTrusted(s_repo.Worktree));
        Assert.IsTrue(TrustIn(dir, Path.Combine(s_repo.Main, "tools")).IsTrusted(Path.Combine(s_repo.Worktree, "tools")));
        Assert.IsFalse(TrustIn(dir, Path.Combine(s_repo.Main, "tools")).IsTrusted(s_repo.Worktree));
        Assert.IsFalse(TrustIn(dir, dir.Root).IsTrusted(s_repo.Worktree));
    }

    [TestMethod]
    public void AForgedGitFileGivesNoTrust()
    {
        using var dir = new TempDir();
        var trust = TrustIn(dir, s_repo.Main);
        var admin = Path.Combine(s_repo.Main, ".git", "worktrees", "wt");
        Write(dir.Path("copy", ".git"), $"gitdir: {admin}\n");
        var outside = dir.Path("fake", ".git", "worktrees", "x");
        Write(Path.Combine(outside, "HEAD"), "ref: refs/heads/main\n");
        Write(Path.Combine(outside, "commondir"), Path.Combine(s_repo.Main, ".git") + "\n");
        Write(Path.Combine(outside, "gitdir"), dir.Path("own", ".git") + "\n");
        Write(dir.Path("own", ".git"), $"gitdir: {outside}\n");

        foreach (var forged in new[] { dir.Path("copy"), dir.Path("own") })
        {
            Assert.AreEqual(CheckoutKind.Main, Checkout.Read(forged)!.Kind, forged);
            Assert.IsFalse(trust.IsTrusted(forged), forged);
        }
    }

    private static TrustStore TrustIn(TempDir dir, string folder) =>
        new(new Settings { TrustedFolders = [folder] }, dir.Path("settings.json"));

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
