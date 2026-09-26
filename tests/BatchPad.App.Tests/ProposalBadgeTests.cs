using BatchPad.App.ViewModels;
using BatchPad.App.ViewModels.Editor;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ProposalBadgeTests
{
    [TestMethod]
    public async Task NewOptionInTheHeaderRaisesABadgeAfterAFolderChange()
    {
        using var test = new TestWorkspace();
        var (main, scripts) = await Open(test, ("tool.bat", "@echo off\r\nrem Builds the tool.\r\necho done\r\n"));
        Assert.IsNull(Node(main, "tool.bat").ProposalBadge);

        File.WriteAllText(Path.Combine(scripts, "tool.bat"), "@echo off\r\nrem   tool.bat [--gated]\r\necho done\r\n");
        main.CheckForExternalChanges();
        await main.ProposalScan;

        Assert.AreEqual("1 new option: --gated", Node(main, "tool.bat").ProposalBadge);
    }

    [TestMethod]
    public async Task DismissingClearsTheBadgeAcrossAReload()
    {
        using var test = new TestWorkspace();
        var (main, _) = await Open(test, ("tool.bat", "@echo off\r\nrem   tool.bat [--gated]\r\n"));
        var node = Node(main, "tool.bat");
        Assert.IsTrue(node.HasProposals);

        main.Details.ShowProposalsCommand.Execute(node);
        var editor = main.Details.Editor!;
        Assert.AreEqual(ScriptEditorViewModel.ParametersTabIndex, editor.SelectedTab);
        editor.Parameters.Proposals.Single(p => p.Parameter?.Arg == "--gated").DismissCommand.Execute(null);
        editor.CancelCommand.Execute(null);
        main.Reload();
        await main.ProposalScan;

        Assert.IsNull(Node(main, "tool.bat").ProposalBadge);
    }

    [TestMethod]
    public async Task ServeAndStopPairProposesStopAndLongRunning()
    {
        using var test = new TestWorkspace();
        var (main, _) = await Open(test,
            ("serve_web.bat", "@echo off\r\npython -m http.server %~1\r\n"),
            ("stop_web.bat", "@echo off\r\necho stopping %~1\r\n"));
        var serve = Node(main, "serve_web.bat");
        StringAssert.Contains(serve.ProposalBadge, "long-running");
        StringAssert.Contains(serve.ProposalBadge, "stop with stop_web.bat");

        main.Details.ShowProposalsCommand.Execute(serve);
        var editor = main.Details.Editor!;
        editor.Parameters.Proposals.Single(p => p.Key == "longRunning").AcceptCommand.Execute(null);
        editor.Parameters.Proposals.Single(p => p.Key == "stop:stop_web.bat").AcceptCommand.Execute(null);
        editor.SaveCommand.Execute(null);

        var entries = ConfigReader.ReadFile(main.Workspace!.FilePath).Scripts.OfType<ScriptNode>().ToList();
        var saved = entries.Single(s => s.Path == ".batchpad/scripts/serve_web.bat");
        Assert.IsTrue(saved.LongRunning);
        Assert.AreEqual("stop-web", saved.Stop);
        Assert.AreEqual("stop-web", entries.Single(s => s.Path == ".batchpad/scripts/stop_web.bat").Id);
        await main.ProposalScan;
        StringAssert.DoesNotMatch(Node(main, "serve_web.bat").ProposalBadge ?? "", new System.Text.RegularExpressions.Regex("stop|long-running"));
    }

    private static async Task<(MainViewModel, string)> Open(TestWorkspace test, params (string Name, string Content)[] files)
    {
        var demo = test.CopyDemo();
        var scripts = Directory.CreateDirectory(Path.Combine(demo, ".batchpad", "scripts")).FullName;
        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(scripts, name), content);
        var main = test.OpenMain(demo, trusted: true);
        await main.ProposalScan;
        return (main, scripts);
    }

    private static NodeViewModel Node(MainViewModel main, string fileName) =>
        main.Tree!.AllNodes.Single(n => n.Kind == NodeKind.Script && Path.GetFileName(n.FilePath ?? "") == fileName);
}
