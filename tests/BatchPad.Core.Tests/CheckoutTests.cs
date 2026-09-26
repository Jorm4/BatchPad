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

    [TestMethod]
    [DataRow(@"\\batchpad-test.invalid\share\wt")]
    [DataRow("//batchpad-test.invalid/share/wt")]
    [DataRow(@"\\?\UNC\batchpad-test.invalid\share\wt")]
    [DataRow(@"\\.\pipe\wt")]
    public void AGitFileNamingANetworkPathIsNeverFollowed(string target)
    {
        using var dir = new TempDir();
        Write(dir.Path("untrusted", ".git"), $"gitdir: {target}\n");

        var located = Checkout.Locate(dir.Path("untrusted"))!;

        Assert.IsNull(located.GitDirectory);
        Assert.AreEqual(CheckoutKind.Main, located.Kind);
        Assert.IsFalse(TrustIn(dir, s_repo.Main).IsTrusted(dir.Path("untrusted")));
    }

    [TestMethod]
    public void OnlyPathsOnTheNamingFilesOwnDriveAreFollowed()
    {
        using var dir = new TempDir();
        var file = dir.Path(".git");
        var otherDrive = (char.ToUpperInvariant(dir.Root[0]) == 'Q' ? "R" : "Q") + @":\repo\.git";

        Assert.AreEqual(dir.Path("admin"), GitInfo.LocalPathNamedIn(file, "admin"));
        Assert.AreEqual(dir.Path("admin"), GitInfo.LocalPathNamedIn(file, dir.Path("admin")));
        Assert.IsNull(GitInfo.LocalPathNamedIn(file, otherDrive));
        Assert.IsNull(GitInfo.LocalPathNamedIn(@"\\server\share\.git", "admin"));
    }

    [TestMethod]
    public void AWorktreeWhoseAdminFilesNameANetworkPathIsNotVerified()
    {
        using var dir = new TempDir();
        var trust = TrustIn(dir, s_repo.Main);
        var admin = dir.Path("repo", ".git", "worktrees", "x");
        Write(Path.Combine(admin, "HEAD"), "ref: refs/heads/main\n");
        Write(Path.Combine(admin, "commondir"), @"\\batchpad-test.invalid\share\.git" + "\n");
        Write(Path.Combine(admin, "gitdir"), dir.Path("x", ".git") + "\n");
        Write(dir.Path("x", ".git"), $"gitdir: {admin}\n");

        Assert.AreEqual(CheckoutKind.Main, Checkout.Read(dir.Path("x"))!.Kind);
        Assert.IsFalse(trust.IsTrusted(dir.Path("x")));
    }

    [TestMethod]
    public void TrustStoredAsAShortPathCoversTheRepositorysWorktrees()
    {
        var shortMain = ShortPath(s_repo.Main);
        if (shortMain.Equals(s_repo.Main, StringComparison.OrdinalIgnoreCase))
            Assert.Inconclusive("This volume has no 8.3 names.");
        using var dir = new TempDir();

        Assert.IsTrue(TrustIn(dir, shortMain).IsTrusted(s_repo.Worktree));
    }

    private static string ShortPath(string path)
    {
        var buffer = new System.Text.StringBuilder(1024);
        return GetShortPathName(path, buffer, buffer.Capacity) is > 0 and < 1024 ? buffer.ToString() : path;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern int GetShortPathName(string longPath, System.Text.StringBuilder shortPath, int bufferLength);

    private static TrustStore TrustIn(TempDir dir, string folder) =>
        new(new Settings { TrustedFolders = [folder] }, dir.Path("settings.json"));

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
