using System.Text.Json.Nodes;
using BatchPad.App.Cli;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class WorktreeCliTests
{
    private static GitRepo s_repo = null!;

    [ClassInitialize]
    public static void CreateRepository(TestContext _) => s_repo = GitRepo.WithWhichScript();

    [ClassCleanup]
    public static void RemoveRepository() => s_repo.Dispose();

    [TestMethod]
    public async Task RunJsonInAWorktreeRunsItsScriptAndReportsTheWorktree()
    {
        using var test = new TestWorkspace();
        var (runner, output, _) = TrustedRunner(test);

        Assert.AreEqual(0, await runner.RunAsync(["run", "which", "--json"], s_repo.Worktree));

        var result = JsonNode.Parse(output.ToString())!;
        var checkout = result["checkout"]!;
        Assert.AreEqual("worktree", checkout["kind"]!.GetValue<string>());
        Assert.AreEqual("wt", checkout["name"]!.GetValue<string>());
        Assert.AreEqual(GitRepo.WorktreeBranch, checkout["branch"]!.GetValue<string>());
        StringAssert.Contains(File.ReadAllText(result["logPath"]!.GetValue<string>()), "worktree");
    }

    [TestMethod]
    public async Task ATextRunNamesItsCheckoutOnStderr()
    {
        using var test = new TestWorkspace();
        var (runner, output, error) = TrustedRunner(test);

        Assert.AreEqual(0, await runner.RunAsync(["run", "which"], s_repo.Main));

        StringAssert.Contains(error.ToString(), "in main (main)");
        Assert.AreEqual("main", output.ToString().Trim());
    }

    private static (CliRunner Runner, StringWriter Output, StringWriter Error) TrustedRunner(TestWorkspace test)
    {
        var settings = new Settings();
        new TrustStore(settings, test.Paths.SettingsFile).Trust(s_repo.Main);
        var output = new StringWriter();
        var error = new StringWriter();
        return (new CliRunner(test.Paths, settings, output, error), output, error);
    }
}
