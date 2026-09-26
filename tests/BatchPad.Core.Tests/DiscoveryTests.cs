using BatchPad.Core.Discovery;
using BatchPad.Core.Model;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class DiscoveryTests
{
    private static readonly ScriptFolder[] RootAndTools =
    [
        new() { Path = ".", Include = ["*.bat"], Recurse = false },
        new() { Path = "tools", Include = ["*.py", "*.bat"], Exclude = ["test_*", "*_lib.py"] },
    ];

    private static TempDir WithFiles(params string[] relativePaths)
    {
        var dir = new TempDir();
        foreach (var relative in relativePaths)
        {
            var path = dir.Path(relative.Split('/'));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }
        return dir;
    }

    private static List<TreeItem> Merge(TempDir dir, IEnumerable<TreeNode> entries, IEnumerable<ScriptFolder>? folders = null,
        IReadOnlySet<string>? seen = null) =>
        TreeMerger.Merge(entries, dir.Root, ScriptFolderScanner.Scan(dir.Root, folders ?? RootAndTools), seen);

    private static IEnumerable<TreeItem> Flatten(IEnumerable<TreeItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children)));

    private static List<string> Paths(IEnumerable<TreeItem> items) =>
        [.. Flatten(items).Where(i => i.ScriptPath is not null).Select(i => i.ScriptPath!).Order()];

    [TestMethod]
    public void IncludeExcludeAndRecurseSelectFiles()
    {
        using var dir = WithFiles("build.bat", "notes.txt", "sub/deep.bat", "tools/regen.py", "tools/test_x.py",
            "tools/io_lib.py", "tools/web/serve.bat", "tools/.hidden/x.py");

        CollectionAssert.AreEqual(new[] { "build.bat", "tools/regen.py", "tools/web/serve.bat" }, Paths(Merge(dir, [])));
    }

    [TestMethod]
    public void NewFileAppearsOnRescan()
    {
        using var dir = WithFiles("build.bat");
        Assert.HasCount(1, Paths(Merge(dir, [])));

        File.WriteAllText(dir.Path("package.bat"), "");

        CollectionAssert.AreEqual(new[] { "build.bat", "package.bat" }, Paths(Merge(dir, [])));
    }

    [TestMethod]
    public void SubFoldersBecomeTreeFolders()
    {
        using var dir = WithFiles("tools/web/serve.bat");

        var folder = Merge(dir, []).Single();

        Assert.IsInstanceOfType<FolderNode>(folder.Node);
        Assert.AreEqual("web", folder.Name);
        Assert.AreEqual("Serve", folder.Children.Single().Name);
    }

    [TestMethod]
    public void GroupByPrefixGroupsSharedPrefixesOnly()
    {
        using var dir = WithFiles("s/build_game.bat", "s/build_tools.bat", "s/package_web.bat");

        var items = Merge(dir, [], [new ScriptFolder { Path = "s", GroupByPrefix = true }]);

        var group = items.Single(i => i.Node is FolderNode);
        Assert.AreEqual("Build", group.Name);
        CollectionAssert.AreEqual(new[] { "Build game", "Build tools" }, group.Children.Select(c => c.Name).ToList());
        Assert.AreEqual("Package web", items.Single(i => i.Node is ScriptNode).Name);
    }

    [TestMethod]
    public void ExplicitEntryOverridesDiscoveredName()
    {
        using var dir = WithFiles("build.bat");
        var entry = new ScriptNode { Id = "build", Name = "Build everything", Path = "build.bat" };

        var items = Merge(dir, [new FolderNode { Folder = "Build", Items = [entry] }]);

        var item = Flatten(items).Single(i => i.ScriptPath == "build.bat");
        Assert.AreEqual("Build everything", item.Name);
        Assert.AreSame(entry, item.Node);
        Assert.IsTrue(item.HasEntry && item.IsDiscovered && !item.IsOrphan);
        Assert.HasCount(1, items);
    }

    [TestMethod]
    public void HiddenEntryRemovesScript()
    {
        using var dir = WithFiles("build.bat", "clean.bat");

        var items = Merge(dir, [new ScriptNode { Path = "./clean.bat", Hidden = true }]);

        CollectionAssert.AreEqual(new[] { "build.bat" }, Paths(items));
    }

    [TestMethod]
    public void DeletedFileBecomesOrphan()
    {
        using var dir = WithFiles("build.bat");
        var entries = new TreeNode[]
        {
            new ScriptNode { Name = "Build", Path = "build.bat" },
            new ScriptNode { Name = "Run", Runner = Runner.Exe, Path = "bin/game.exe" },
            new ScriptNode { Name = "Run cfg", Path = "build/${param:config.dir}/x.exe" },
        };
        Assert.IsFalse(Flatten(Merge(dir, entries)).Any(i => i.IsOrphan));

        File.Delete(dir.Path("build.bat"));

        var orphan = Flatten(Merge(dir, entries)).Single(i => i.IsOrphan);
        Assert.AreEqual("Build", orphan.Name);
    }

    [TestMethod]
    public void UnseenDiscoveredScriptsAreNew()
    {
        using var dir = WithFiles("build.bat", "package.bat");

        var items = Merge(dir, [], seen: new HashSet<string>(["build.bat"], StringComparer.OrdinalIgnoreCase));

        CollectionAssert.AreEqual(new[] { "package.bat" }, items.Where(i => i.IsNew).Select(i => i.ScriptPath).ToList());
        Assert.IsFalse(Merge(dir, []).Any(i => i.IsNew));
    }

    [TestMethod]
    public void DefaultFolderIsBatchpadScripts()
    {
        using var dir = WithFiles(".batchpad/scripts/tidy.py", "other.py");

        var items = Merge(dir, [], ScriptFolderScanner.DefaultFolders);

        CollectionAssert.AreEqual(new[] { ".batchpad/scripts/tidy.py" }, Paths(items));
    }

    [TestMethod]
    public void IdFromFileNameAddsSuffixOnCollision()
    {
        var taken = new HashSet<string>();
        var first = IdAssigner.FromFileName("tools/regen_assets.py", taken);
        taken.Add(first);

        Assert.AreEqual("regen-assets", first);
        Assert.AreEqual("regen-assets-2", IdAssigner.FromFileName("other/regen_assets.bat", taken));
        Assert.AreEqual("build-web", IdAssigner.FromFileName("Build Web!.bat", taken));
    }

    [TestMethod]
    public void WatcherRaisesFolderChanged()
    {
        using var dir = new TempDir();
        using var changed = new ManualResetEventSlim();
        using var watcher = new FolderWatcher([dir.Root, dir.Path("missing")], TimeSpan.FromMilliseconds(20));
        watcher.FolderChanged += (_, _) => changed.Set();

        File.WriteAllText(dir.Path("new.bat"), "");

        Assert.IsTrue(changed.Wait(TimeSpan.FromSeconds(5)));
    }
}
