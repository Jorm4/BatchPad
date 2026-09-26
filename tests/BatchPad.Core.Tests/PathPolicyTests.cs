using BatchPad.Core.Choices;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class PathPolicyTests
{
    private const string Root = @"C:\repo";

    [TestMethod]
    [DataRow(@"C:\repo\tools\x.json")]
    [DataRow(@"C:\repo")]
    public void InsideTheRootIsAllowed(string path) => Assert.IsNull(PathPolicy.Workspace(Root, trusted: false).Problem(path));

    [TestMethod]
    public void OutsideTheRootNeedsTrust()
    {
        Assert.IsNotNull(PathPolicy.Workspace(Root, trusted: false).Problem(@"C:\repository\x.json"));
        Assert.IsNotNull(PathPolicy.Workspace(Root, trusted: false).Problem(@"C:\Users\me\secrets.txt"));
        Assert.IsNull(PathPolicy.Workspace(Root, trusted: true).Problem(@"C:\Users\me\notes.txt"));
    }

    [TestMethod]
    [DataRow(@"\\server\share\x.json")]
    [DataRow(@"//server/share/x.json")]
    [DataRow(@"\\?\C:\repo\x.json")]
    [DataRow(@"\\.\pipe\x")]
    [DataRow(@"C:\repo\nul")]
    [DataRow(@"C:\repo\tools\CON.txt")]
    public void NetworkAndDevicePathsAreRefusedEvenWhenTrusted(string path) =>
        Assert.IsNotNull(PathPolicy.Workspace(Root, trusted: true).Problem(path));

    [TestMethod]
    public void AWorkspaceOnAShareMayUseItsOwnFolder()
    {
        var policy = PathPolicy.Workspace(@"\\server\share\repo", trusted: false);

        Assert.IsNull(policy.Problem(@"\\server\share\repo\tools\x.json"));
        Assert.IsNotNull(policy.Problem(@"\\server\share\other\x.json"));
    }

    [TestMethod]
    public void UntrustedIncludesAndScriptFoldersStayInsideTheWorkspace()
    {
        using var dir = new TempDir();
        var workspace = Directory.CreateDirectory(dir.Path("repo")).FullName;
        Directory.CreateDirectory(dir.Path("outside", "scripts"));
        File.WriteAllText(dir.Path("outside", "scripts", "leak.bat"), "@echo off");
        File.WriteAllText(dir.Path("outside", "part.json"), """{ "scripts": [ { "id": "outside", "command": "echo" } ] }""");
        File.WriteAllText(Path.Combine(workspace, "batchpad.json"), """
            {
              "include": ["../outside/part.json", "\\\\server\\share\\part.json"],
              "scriptFolders": [ { "path": "../outside/scripts" }, { "path": "\\\\server\\share\\scripts" } ]
            }
            """);
        var paths = new AppPaths(dir.Path("data"));
        var file = Path.Combine(workspace, "batchpad.json");

        var untrusted = WorkspaceLoader.Load(file, paths);

        Assert.IsEmpty(untrusted.Workspace.Parts);
        Assert.IsEmpty(untrusted.Workspace.ScriptFolders);
        Assert.IsEmpty(untrusted.Workspace.Items);
        Assert.IsEmpty(untrusted.ScriptDirectories);
        Assert.HasCount(4, untrusted.Errors);

        var trust = new TrustStore(new Settings { TrustedFolders = [workspace] }, dir.Path("settings.json"));
        var trusted = WorkspaceLoader.Load(file, paths, trust);

        Assert.HasCount(1, trusted.Workspace.Parts);
        Assert.AreEqual(Path.GetFullPath(dir.Path("outside", "scripts")), trusted.ScriptDirectories.Single());
        Assert.HasCount(2, trusted.Errors);
        Assert.IsTrue(trusted.Errors.All(e => e.Message.Contains("network")));
    }

    [TestMethod]
    public void UntrustedChoiceSourcesStayInsideTheWorkspace()
    {
        using var dir = new TempDir();
        var workspace = Directory.CreateDirectory(dir.Path("repo")).FullName;
        File.WriteAllText(dir.Path("secret.txt"), "token=abc");
        var context = new ChoiceContext(workspace) { Paths = PathPolicy.Workspace(workspace, trusted: false) };
        var parameter = new ParameterDefinition
        {
            ChoicesFrom =
            [
                new ChoiceSource { File = "../secret.txt", Regex = "token=(.*)" },
                new ChoiceSource { Glob = "../*.txt" },
                new ChoiceSource { File = @"\\server\share\x.txt", Regex = "." },
            ],
        };

        var result = new ChoiceResolver().Resolve(parameter, context);

        Assert.IsEmpty(result.Choices);
        Assert.HasCount(3, result.Problems);
    }

    [TestMethod]
    public void ChoiceSourcesNeverReachAShareByDefault()
    {
        var parameter = new ParameterDefinition { ChoicesFrom = [new ChoiceSource { File = @"\\server\share\x.txt", Regex = "." }] };

        var result = new ChoiceResolver().Resolve(parameter, new ChoiceContext(Fixtures.Path("choices")));

        StringAssert.Contains(result.Problems.Single(), "network");
    }
}
