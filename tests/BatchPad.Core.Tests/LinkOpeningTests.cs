using System.Text.RegularExpressions;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class LinkOpeningTests
{
    private static readonly TemplateContext Templates = new() { Environment = name => name == "SECRET" ? "hunter2" : null };

    [TestMethod]
    public void ReadyGroupsFromOutputAreNeverExpanded()
    {
        var match = new Regex("on (.+)").Match("Serving on http://x/${env:SECRET}");

        Assert.AreEqual("http://x/${env:SECRET}", ReadyWatcher.Expand("$1", match, Templates));
        Assert.AreEqual("http://x/${env:SECRET}?k=hunter2", ReadyWatcher.Expand("$1?k=${env:SECRET}", match, Templates));
    }

    [TestMethod]
    public void AnOverlongGroupNumberIsEmpty()
    {
        var match = new Regex("(x)").Match("x");

        Assert.AreEqual("a--b", ReadyWatcher.Expand("a-$99999999999-b", match, Templates));
    }

    [TestMethod]
    [DataRow("file:///C:/Windows/System32/calc.exe")]
    [DataRow("ms-settings:privacy")]
    [DataRow(@"\\attacker\share\page.html")]
    [DataRow("file://attacker/share/page.html")]
    public async Task AReadyUrlThatIsAProgramOrOnTheNetworkDoesNotOpen(string url)
    {
        var run = new FakeRun();
        var opener = new FakeOpener();
        using var watcher = ReadyWatcher.Watch(run, new ReadyDefinition { Pattern = "on (.+)", Open = "$1" }, Templates, opener);

        run.Emit($"Serving on {url}");
        var signal = await watcher.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.AreEqual(url, signal!.Url);
        Assert.IsEmpty(opener.Targets);
    }

    [TestMethod]
    public void AnExecutableArtifactIsNotOpenedAfterTheRun()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("setup.bat"), "");
        File.WriteAllText(dir.Path("report.html"), "");
        var opener = new FakeOpener();

        var opened = ArtifactResolver.OpenAfter(new RunResult(RunOutcome.Exited, 0, TimeSpan.Zero),
            [new ResolvedArtifact(dir.Path("setup.bat"), ArtifactOpen.Always), new ResolvedArtifact(dir.Path("report.html"), ArtifactOpen.Always)],
            opener);

        CollectionAssert.AreEqual(new[] { dir.Path("report.html") }, opener.Targets.ToArray());
        Assert.HasCount(1, opened);
    }

    [TestMethod]
    public void TheShellOpensWebLinksAndExistingFilesAndProgramsOnlyOnceConfirmed()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("setup.bat"), "");
        File.WriteAllText(dir.Path("report.html"), "");

        Assert.IsTrue(LinkPolicy.MayOpen("https://example.com/", confirmed: false));
        Assert.IsTrue(LinkPolicy.MayOpen("mailto:a@example.com", confirmed: false));
        Assert.IsTrue(LinkPolicy.MayOpen(dir.Path("report.html"), confirmed: false));
        Assert.IsTrue(LinkPolicy.MayOpen(dir.Root, confirmed: false));
        Assert.IsFalse(LinkPolicy.MayOpen(dir.Path("missing.html"), confirmed: false));
        Assert.IsFalse(LinkPolicy.MayOpen(dir.Path("setup.bat"), confirmed: false));
        Assert.IsTrue(LinkPolicy.MayOpen(dir.Path("setup.bat"), confirmed: true));
        Assert.IsFalse(LinkPolicy.MayOpen("ms-settings:privacy", confirmed: false));
        Assert.IsTrue(LinkPolicy.MayOpen("ms-settings:privacy", confirmed: true));
    }
}
