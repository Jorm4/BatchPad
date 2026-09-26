using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class WorkspaceLocatorTests
{
    [TestMethod]
    public void FindsWorkspaceByWalkingUpFromSubFolder()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), "{}");
        var sub = Directory.CreateDirectory(dir.Path("src", "deep")).FullName;

        Assert.AreEqual(dir.Path("batchpad.json"), WorkspaceLocator.Locate(null, sub, []));
    }

    [TestMethod]
    public void CommandLineFolderWinsOverCurrentDirectory()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("batchpad.json"), "{}");
        Directory.CreateDirectory(dir.Path("other"));

        Assert.AreEqual(dir.Path("other", "batchpad.json"), WorkspaceLocator.Locate("other", dir.Root, []));
    }

    [TestMethod]
    public void FallsBackToFirstExistingRecent()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("recent.json"), "{}");
        var empty = Directory.CreateDirectory(dir.Path("empty")).FullName;

        // Guards against a batchpad.json somewhere above the temp folder.
        var expected = WorkspaceLocator.FindUpwards(empty) ?? dir.Path("recent.json");

        Assert.AreEqual(expected, WorkspaceLocator.Locate(null, empty, [dir.Path("gone.json"), dir.Path("recent.json")]));
    }

    [TestMethod]
    public void PortableMarkerMovesDataNextToExe()
    {
        using var dir = new TempDir();
        var exeDir = Directory.CreateDirectory(dir.Path("app")).FullName;
        var appData = dir.Path("roaming");
        var localAppData = dir.Path("local");

        var installed = AppPaths.Resolve(exeDir, appData, localAppData);
        Assert.AreEqual(Path.Combine(appData, "BatchPad"), installed.DataDirectory);
        Assert.AreEqual(Path.Combine(localAppData, "BatchPad"), installed.LocalDirectory);

        File.WriteAllText(Path.Combine(exeDir, AppPaths.PortableMarker), "");
        var portable = AppPaths.Resolve(exeDir, appData, localAppData);

        Assert.AreEqual(Path.Combine(exeDir, "data", "settings.json"), portable.SettingsFile);
        Assert.AreEqual(Path.Combine(exeDir, "data", "local"), portable.LocalDirectory);
    }

    [TestMethod]
    public void SettingsRoundTripAndRecentsStayUniqueNewestFirst()
    {
        using var dir = new TempDir();
        new Settings().Update(dir.Path("settings.json"), settings =>
        {
            settings.Interpreters["python"] = @"C:\py\python.exe";
            settings.AddRecentWorkspace(dir.Path("a.json"));
            settings.AddRecentWorkspace(dir.Path("b.json"));
            settings.AddRecentWorkspace(dir.Path("A.json"));
            settings.TrustedFolders.Add(dir.Root);
        });

        var reread = Settings.Load(dir.Path("settings.json"));

        CollectionAssert.AreEqual(new[] { dir.Path("A.json"), dir.Path("b.json") }, reread.RecentWorkspaces);
        Assert.AreEqual(@"C:\py\python.exe", reread.Interpreters["python"]);
        CollectionAssert.AreEqual(new[] { dir.Root }, reread.TrustedFolders);
        Assert.IsEmpty(Settings.Load(dir.Path("missing.json")).RecentWorkspaces);
    }

    [TestMethod]
    public void TwoWindowsSavingSettingsKeepEachOthersChanges()
    {
        using var dir = new TempDir();
        var file = dir.Path("settings.json");
        var first = new TrustStore(new Settings(), file);
        var second = new TrustStore(new Settings(), file);

        first.Trust(dir.Path("a"));
        second.Trust(dir.Path("b"));
        new Settings().Update(file, s => s.KeepRunningInTray = false);

        var saved = Settings.Load(file);
        CollectionAssert.AreEqual(new[] { dir.Path("a"), dir.Path("b") }, saved.TrustedFolders);
        Assert.IsFalse(saved.KeepRunningInTray);
    }
}
