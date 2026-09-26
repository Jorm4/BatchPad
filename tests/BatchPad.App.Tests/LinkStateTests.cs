using BatchPad.App.ViewModels;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Workspace;
using Microsoft.Extensions.Time.Testing;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class LinkStateTests
{
    private static readonly DateTime Written = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [TestMethod]
    public void LocalFileLinkShowsItsAge()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        File.WriteAllText(Path.Combine(demo, "report.html"), "<p>ok</p>");
        File.SetLastWriteTimeUtc(Path.Combine(demo, "report.html"), Written);
        var main = Open(test, demo, new LinkNode { Name = "Report", Url = "report.html" });

        var link = Link(main, "Report");
        Assert.AreEqual("updated 2 h ago", link.LinkAge);
        Assert.IsFalse(link.IsMissing);
    }

    [TestMethod]
    public void MissingFileMarksTheLink()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var main = Open(test, demo, new LinkNode { Name = "Gone", Url = "gone.html" }, new LinkNode { Name = "Web", Url = "https://example.com" });

        Assert.IsTrue(Link(main, "Gone").IsMissing);
        Assert.IsNull(Link(main, "Gone").LinkAge);
        Assert.IsFalse(Link(main, "Web").IsMissing);
    }

    [TestMethod]
    public void PinAsLinkAddsTheArtifactToMyScripts()
    {
        using var test = new TestWorkspace();
        var demo = test.CopyDemo();
        var launcher = new FakeLauncher();
        ConfigWriter.Update(Path.Combine(demo, "batchpad.json"), file => file.Scripts.Add(new ScriptNode
        {
            Id = "report", Name = "Report", Path = "hello.bat", Artifacts = [new ArtifactDefinition { Path = "out/report.html" }],
        }));
        var main = new MainViewModel(test.Paths, new Settings(), launcher, shell: new FakeShell());
        main.Trust.Trust(demo);
        main.OpenInitial(demo, test.Root);
        main.Tree!.AllNodes.Single(n => n.Name == "Report").IsSelected = true;
        main.Details.RunCommand.Execute(null);

        var artifact = ((RunViewModel)main.Output.Tabs.Single()).Artifacts.Single();
        artifact.PinCommand.Execute(null);

        var link = main.Tree!.MyScriptsRoot.Children.Single();
        Assert.AreEqual(NodeKind.Link, link.Kind);
        Assert.AreEqual("report.html", link.Name);
        Assert.AreEqual(Path.Combine(demo, "out", "report.html"), ((LinkNode)link.Node!).Url, ignoreCase: true);
    }

    private static MainViewModel Open(TestWorkspace test, string demo, params LinkNode[] links)
    {
        ConfigWriter.Update(Path.Combine(demo, "batchpad.json"), file => file.Scripts.AddRange(links));
        var main = new MainViewModel(test.Paths, new Settings(), time: new FakeTimeProvider(new DateTimeOffset(Written.AddHours(2))));
        main.OpenInitial(demo, test.Root);
        return main;
    }

    private static NodeViewModel Link(MainViewModel main, string name) =>
        main.Tree!.AllNodes.Single(n => n.Kind == NodeKind.Link && n.Name == name);
}
