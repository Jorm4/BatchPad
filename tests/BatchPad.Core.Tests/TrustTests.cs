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
        var paths = new AppPaths(dir.Root, isPortable: false);
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
        var result = await run.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, result.ExitCode);
    }

    [TestMethod]
    public void TrustSurvivesASettingsRoundTrip()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path("data"), isPortable: false);
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
