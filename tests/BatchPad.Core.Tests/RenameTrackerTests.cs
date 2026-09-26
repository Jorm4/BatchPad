using BatchPad.Core.Config;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class RenameTrackerTests
{
    [TestMethod]
    public async Task RenameSeenByTheWatcherMovesTheEntryAndKeepsItsParams()
    {
        using var dir = new TempDir();
        var workspace = Create(dir);
        var tracker = new RenameTracker();
        var renamed = new TaskCompletionSource();
        using (var watcher = new FolderWatcher([dir.Path(".batchpad", "scripts")], TimeSpan.FromMilliseconds(50)))
        {
            watcher.FileChanged += (_, change) =>
            {
                tracker.Observe(change);
                if (change.Kind == WatcherChangeTypes.Renamed)
                    renamed.TrySetResult();
            };
            File.Move(dir.Path(".batchpad", "scripts", "a.bat"), dir.Path(".batchpad", "scripts", "b.bat"));
            await renamed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.IsTrue(tracker.Apply(workspace));

        var entry = ConfigReader.ReadFile(workspace.FilePath).Scripts.OfType<ScriptNode>().Single();
        Assert.AreEqual(".batchpad/scripts/b.bat", entry.Path);
        Assert.AreEqual("mode", entry.Params!.Single().Name);
        var user = ConfigReader.ReadFile(workspace.MyScripts.FilePath);
        CollectionAssert.AreEqual(new[] { ".batchpad/scripts/b.bat" }, user.SeenPaths);
        Assert.IsTrue(user.DismissedProposals!.ContainsKey("workspace:.batchpad/scripts/b.bat"));
    }

    [TestMethod]
    public void RenameToABackupWithANewFileInPlaceMovesNothing()
    {
        using var dir = new TempDir();
        var workspace = Create(dir);
        var script = dir.Path(".batchpad", "scripts", "a.bat");
        File.Move(script, script + "~");
        File.WriteAllText(script, "@echo new");
        var tracker = new RenameTracker();
        tracker.Add(new FileRename(script, script + "~"));

        Assert.IsFalse(tracker.Apply(workspace));
        Assert.AreEqual(".batchpad/scripts/a.bat", ConfigReader.ReadFile(workspace.FilePath).Scripts.OfType<ScriptNode>().Single().Path);
    }

    [TestMethod]
    public void RenamingTheFolderMovesTheEntriesInIt()
    {
        using var dir = new TempDir();
        var workspace = Create(dir);
        Directory.Move(dir.Path(".batchpad", "scripts"), dir.Path(".batchpad", "tools"));
        var tracker = new RenameTracker();
        tracker.Add(new FileRename(dir.Path(".batchpad", "scripts"), dir.Path(".batchpad", "tools")));

        Assert.IsTrue(tracker.Apply(workspace));
        Assert.AreEqual(".batchpad/tools/a.bat", ConfigReader.ReadFile(workspace.FilePath).Scripts.OfType<ScriptNode>().Single().Path);
    }

    private static LoadedWorkspace Create(TempDir dir)
    {
        Directory.CreateDirectory(dir.Path(".batchpad", "scripts"));
        File.WriteAllText(dir.Path(".batchpad", "scripts", "a.bat"), "@echo off");
        var file = dir.Path("batchpad.json");
        ConfigWriter.Write(file, new WorkspaceFile
        {
            Id = "rename-test",
            Scripts = [new ScriptNode { Path = ".batchpad/scripts/a.bat", Params = [new ParameterDefinition { Name = "mode", Type = ParameterType.Text }] }],
        });
        var paths = new AppPaths(dir.Path("data"), isPortable: true);
        ConfigWriter.Write(paths.UserFile("rename-test"), new WorkspaceFile
        {
            SeenPaths = [".batchpad/scripts/a.bat"],
            DismissedProposals = new() { ["workspace:.batchpad/scripts/a.bat"] = ["--gated"] },
        });
        return WorkspaceLoader.Load(file, paths);
    }
}
