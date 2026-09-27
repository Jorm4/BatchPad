using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TrustTests
{
    [TestMethod]
    public async Task ARunIsRefusedBeforeTrustAndAllowedAfter()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Root);
        var workspace = WorkspaceLoader.Load(Path.Combine(Fixtures.DemoWorkspace, "batchpad.json"), paths);
        var script = workspace.Workspace.AllNodes().Select(n => n.Node).OfType<ScriptNode>().Single(s => s.Id == "hello-bat");
        var request = new RunRequest(workspace, workspace.Workspace, script);
        var trust = TrustStore.Load(paths);
        var gate = new RunGate(trust);

        var refusal = Assert.ThrowsExactly<UntrustedWorkspaceException>(() => gate.Start(request, new InterpreterLocator()));
        StringAssert.Contains(refusal.Message, workspace.Directory);
        Assert.IsFalse(gate.Check(workspace).Allowed);

        trust.Trust(workspace.Directory);
        using var run = gate.Start(request, new InterpreterLocator());
        var result = await run.Completion.WaitAsync(Limit);

        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public void AScriptIncludedFromOutsideTheWorkspaceNeedsThatFolderTrusted()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path("repo"));
        Directory.CreateDirectory(dir.Path("shared"));
        File.WriteAllText(dir.Path("shared", "batchpad.json"), """{ "scripts": [ { "id": "outside", "command": "exit 0" } ] }""");
        File.WriteAllText(dir.Path("repo", "batchpad.json"), """{ "id": "inc", "include": [ "../shared/batchpad.json" ] }""");
        var paths = new AppPaths(dir.Path("data"));
        var trust = TrustStore.Load(paths);
        trust.Trust(dir.Path("repo"));
        var workspace = WorkspaceLoader.Load(dir.Path("repo", "batchpad.json"), paths, trust);
        var part = workspace.Workspace.Parts.Single();
        var request = new RunRequest(workspace, part, part.AllNodes().Select(n => n.Node).OfType<ScriptNode>().Single());
        var gate = new RunGate(trust);

        Assert.IsTrue(gate.Check(workspace).Allowed);
        var refusal = Assert.ThrowsExactly<UntrustedWorkspaceException>(() => gate.Start(request, new InterpreterLocator()));
        StringAssert.Contains(refusal.Message, part.BaseDirectory);

        trust.Trust(dir.Path("shared"));
        Assert.IsTrue(gate.Check(request).Allowed);
    }

    [TestMethod]
    public void TrustSurvivesASettingsRoundTrip()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path("data"));
        var workspaceFolder = Directory.CreateDirectory(dir.Path("repo")).FullName;

        TrustStore.Load(paths).Trust(workspaceFolder + Path.DirectorySeparatorChar);
        var reloaded = TrustStore.Load(paths);

        Assert.IsTrue(reloaded.IsTrusted(workspaceFolder));
        Assert.IsTrue(reloaded.IsTrusted(Path.Combine(workspaceFolder, "sub")));
        Assert.IsFalse(reloaded.IsTrusted(workspaceFolder + "-other"));

        reloaded.Revoke(workspaceFolder);
        Assert.IsFalse(TrustStore.Load(paths).IsTrusted(workspaceFolder));
    }

    [TestMethod]
    public void BlankRelativeAndInvalidEntriesAreIgnored()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path("data"));
        var workspaceFolder = Directory.CreateDirectory(dir.Path("repo")).FullName;
        var store = TrustStore.Load(paths);
        store.Trust(workspaceFolder);
        Settings.Load(paths.SettingsFile).Update(paths.SettingsFile, s => s.TrustedFolders.AddRange(["", "  ", ".", "C:\\bad\0path"]));

        var reloaded = TrustStore.Load(paths);

        Assert.IsTrue(reloaded.IsTrusted(workspaceFolder));
        Assert.IsFalse(reloaded.IsTrusted(Environment.CurrentDirectory));
        Assert.IsTrue(reloaded.Revoke(workspaceFolder));
    }

    [TestMethod]
    [DataRow("tools/build.bat", true)]
    [DataRow(@"C:\apps\game.EXE", true)]
    [DataRow(@"C:\Users\me\Desktop\Shortcut.lnk", true)]
    [DataRow("file:///C:/x/run.cmd", true)]
    [DataRow("ms-settings:privacy", true)]
    [DataRow("build/qa/qa_report.html", false)]
    [DataRow("https://example.com/download.exe", false)]
    [DataRow("file:///C:/x/readme.html", false)]
    public void ExecutableLinkTypesAreClassified(string target, bool executable)
    {
        Assert.AreEqual(executable, LinkPolicy.IsExecutable(target));
    }

    [TestMethod]
    public void ExecutableLinksNeedConfirmationAndNeverOpenUntrusted()
    {
        Assert.AreEqual(LinkAction.Confirm, LinkPolicy.Decide("run.bat", workspaceTrusted: true));
        Assert.AreEqual(LinkAction.Refuse, LinkPolicy.Decide("run.bat", workspaceTrusted: false));
        Assert.AreEqual(LinkAction.Open, LinkPolicy.Decide("report.html", workspaceTrusted: false));
    }
}
