using BatchPad.Core.Choices;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class CommandChoiceTests
{
    private static ResolvedChoices Resolve(string workspace, string command, bool trusted, TimeSpan? timeout = null)
    {
        var settings = new Settings { TrustedFolders = trusted ? [workspace] : [] };
        var commands = new CommandChoiceSource(new TrustStore(settings, Path.Combine(workspace, "unused-settings.json")),
            new InterpreterLocator(), timeout);
        var parameter = new ParameterDefinition { ChoicesFrom = [new ChoiceSource { Command = command }] };
        return new ChoiceResolver().Resolve(parameter, new ChoiceContext(workspace) { Commands = commands });
    }

    [TestMethod]
    public void EachNonEmptyOutputLineIsAChoice()
    {
        var result = Resolve(Fixtures.Path("choices"), "tools/list_suites.bat", trusted: true);

        Assert.IsEmpty(result.Problems, string.Join("; ", result.Problems));
        CollectionAssert.AreEqual(new[] { "Alpha", "Beta", "Gamma" }, result.Choices.Select(c => c.Value).ToList());
    }

    [TestMethod]
    public void UntrustedWorkspaceRunsNothing()
    {
        using var temp = new TempDir();
        var marker = temp.Path("ran.txt");
        File.WriteAllText(temp.Path("list.bat"), $"@echo off\r\necho x> \"{marker}\"\r\necho Alpha\r\n");

        var result = Resolve(temp.Root, "list.bat", trusted: false);

        Assert.IsEmpty(result.Choices);
        Assert.HasCount(1, result.Problems);
        StringAssert.Contains(result.Problems[0], "trust");
        Assert.IsFalse(File.Exists(marker));
    }

    [TestMethod]
    public void HangingScriptTimesOutWithAProblem()
    {
        var result = Resolve(Fixtures.Path("choices"), "tools/hang.bat", trusted: true, TimeSpan.FromMilliseconds(500));

        Assert.IsEmpty(result.Choices);
        Assert.HasCount(1, result.Problems);
        StringAssert.Contains(result.Problems[0], "did not finish");
    }

    [TestMethod]
    public void WithoutARunnerCommandSourcesAreAProblem()
    {
        var parameter = new ParameterDefinition { ChoicesFrom = [new ChoiceSource { Command = "tools/list_suites.bat" }] };

        var result = new ChoiceResolver().Resolve(parameter, new ChoiceContext(Fixtures.Path("choices")));

        Assert.HasCount(1, result.Problems);
    }
}
