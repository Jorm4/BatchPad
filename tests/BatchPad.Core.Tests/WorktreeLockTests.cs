using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class WorktreeLockTests
{
    private static GitRepo s_repo = null!;

    [ClassInitialize]
    public static void CreateRepository(TestContext _) => s_repo = new GitRepo(new Dictionary<string, string>
    {
        ["batchpad.json"] = """
            { "id": "worktree-locks", "scripts": [
              { "id": "build", "path": "nap.py", "lock": "native-build" },
              { "id": "serve", "path": "nap.py", "lock": "port", "lockScope": "machine" },
              { "id": "single", "path": "nap.py", "singleInstance": true }
            ] }
            """,
        ["nap.py"] = "import time\ntime.sleep(0.3)\n",
    });

    [ClassCleanup]
    public static void RemoveRepository() => s_repo.Dispose();

    [TestMethod]
    public async Task TheSameLockInTwoWorktreesDoesNotBlock()
    {
        using var test = new Checkouts();
        using var main = test.Start(test.Main, "build");
        using var worktree = test.Start(test.Worktree, "build");

        Assert.IsNull(worktree.WaitingForLock);
        await Task.WhenAll(main.Completion, worktree.Completion).WaitAsync(Limit);
    }

    [TestMethod]
    public async Task TheSameLockInOneCheckoutBlocks()
    {
        using var test = new Checkouts();
        using var first = test.Start(test.Worktree, "build");
        using var second = test.Start(test.Worktree, "build");

        Assert.AreEqual("native-build", second.WaitingForLock);
        await Task.WhenAll(first.Completion, second.Completion).WaitAsync(Limit);
    }

    [TestMethod]
    public async Task AMachineScopedLockBlocksAcrossWorktreesAndNamesTheHoldersCheckout()
    {
        using var test = new Checkouts();
        using var main = test.Start(test.Main, "serve");
        using var worktree = test.Start(test.Worktree, "serve");

        Assert.AreEqual("port", worktree.WaitingForLock);
        var elsewhere = new LockManager(test.LockDirectory);
        StringAssert.StartsWith(elsewhere.DescribeHolder("port", test.Worktree.CheckoutDirectory), "held by serve in repo since ");
        StringAssert.StartsWith(elsewhere.DescribeHolder("port", test.Main.CheckoutDirectory), "held by serve since ");
        await Task.WhenAll(main.Completion, worktree.Completion).WaitAsync(Limit);
    }

    [TestMethod]
    public async Task SingleInstanceIsPerCheckout()
    {
        using var test = new Checkouts();
        using var main = test.Start(test.Main, "single");
        using var worktree = test.Start(test.Worktree, "single");
        using var again = test.Start(test.Main, "single");

        Assert.IsNull(worktree.WaitingForLock);
        Assert.IsNotNull(again.WaitingForLock);
        await Task.WhenAll(main.Completion, worktree.Completion, again.Completion).WaitAsync(Limit);
    }

    private sealed class Checkouts : IDisposable
    {
        private readonly TempDir _data = new();
        private readonly RunGate _gate;

        public Checkouts()
        {
            var paths = new AppPaths(_data.Path("data"));
            var trust = new TrustStore(new Settings { TrustedFolders = [s_repo.Main] }, paths.SettingsFile);
            _gate = new RunGate(trust, locks: new LockManager(LockDirectory));
            Main = WorkspaceLoader.Load(Path.Combine(s_repo.Main, "batchpad.json"), paths, trust);
            Worktree = WorkspaceLoader.Load(Path.Combine(s_repo.Worktree, "batchpad.json"), paths, trust);
        }

        public string LockDirectory => _data.Path("locks");
        public LoadedWorkspace Main { get; }
        public LoadedWorkspace Worktree { get; }

        public RunHandle Start(LoadedWorkspace workspace, string id) => _gate.Start(
            new RunRequest(workspace, workspace.Workspace, (ScriptNode)workspace.References.Resolve(id, TreeKind.Workspace)!),
            RunWorkspace.Interpreters);

        public void Dispose() => _data.Dispose();
    }
}
