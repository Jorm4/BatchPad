using BatchPad.Core.Output;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class SourceLocationParserTests
{
    [TestMethod]
    [DataRow(@"src\a.cpp(12,5): error C2065: 'x': undeclared identifier")]
    [DataRow("src/a.cpp:12:5: error: 'x' was not declared")]
    public void MsvcAndGccReferencesResolveToAnExistingFile(string line)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path("src"));
        File.WriteAllText(dir.Path("src", "a.cpp"), "");

        var reference = SourceLocationParser.Find(line)!.Single();
        var location = SourceLocationParser.Resolve(reference, dir.Root);

        Assert.AreEqual(0, reference.Start);
        Assert.AreEqual(line.IndexOf(": ", StringComparison.Ordinal), reference.Length);
        Assert.AreEqual(new SourceLocation(dir.Path("src", "a.cpp"), 12, 5), location);
    }

    [TestMethod]
    public void AReferenceToAMissingFileResolvesToNothing()
    {
        using var dir = new TempDir();

        var reference = SourceLocationParser.Find(@"src\a.cpp(12,5): error C2065")!.Single();

        Assert.IsNull(SourceLocationParser.Resolve(reference, dir.Root));
        Assert.IsNull(new SourceLocationResolver(() => dir.Root).Resolve(reference));
    }

    [TestMethod]
    public void PlainTextHasNoReferences()
    {
        Assert.IsNull(SourceLocationParser.Find("Build succeeded in 12.5 s"));
        Assert.IsNull(SourceLocationParser.Find("started at 12:30:45"));
    }

    [TestMethod]
    public void TheEditorTemplateIsTheSettingThenVsCodeThenNone()
    {
        var location = new SourceLocation(@"C:\w\a.cpp", 12, 0);

        Assert.AreEqual(@"np -n12 -c1 ""C:\w\a.cpp""", EditorCommand.Expand(EditorCommand.Template("np -n{line} -c{col} \"{file}\"", codeOnPath: true)!, location));
        Assert.AreEqual(@"code -g ""C:\w\a.cpp:12""", EditorCommand.Expand(EditorCommand.Template(null, codeOnPath: true)!, location));
        Assert.IsNull(EditorCommand.Template(" ", codeOnPath: false));
    }
}
