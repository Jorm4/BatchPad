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
        var location = new SourceLocationResolver(() => dir.Root).Resolve(reference);

        Assert.AreEqual(0, reference.Start);
        Assert.AreEqual(line.IndexOf(": ", StringComparison.Ordinal), reference.Length);
        Assert.AreEqual(new SourceLocation(dir.Path("src", "a.cpp"), 12, 5), location);
    }

    [TestMethod]
    public void AReferenceToAMissingFileResolvesToNothing()
    {
        using var dir = new TempDir();

        var reference = SourceLocationParser.Find(@"src\a.cpp(12,5): error C2065")!.Single();

        Assert.IsNull(new SourceLocationResolver(() => dir.Root).Resolve(reference));
    }

    [TestMethod]
    public void PlainTextHasNoReferences()
    {
        Assert.IsNull(SourceLocationParser.Find("Build succeeded in 12.5 s"));
        Assert.IsNull(SourceLocationParser.Find("started at 12:30:45"));
    }

    [TestMethod]
    [DataRow("//localhost/C$/Windows/win.ini(1): x")]
    [DataRow(@"\\localhost\C$\Windows\win.ini(1): x")]
    public void NetworkAndDevicePathsAreNeverLookedUp(string line)
    {
        var reference = SourceLocationParser.Find(line)!.Single();

        Assert.IsNull(new SourceLocationResolver(() => @"C:\").Resolve(reference));
    }

    [TestMethod]
    public void TheEditorTemplateIsTheSettingThenVsCodeThenNotepadForScriptsThenNone()
    {
        var location = new SourceLocation(@"C:\w\a.cpp", 12, 0);
        string Expanded(string template) => EditorCommand.Environment(location)
            .Aggregate(EditorCommand.Expand(template, location), (line, v) => line.Replace($"!{v.Key}!", v.Value));

        Assert.AreEqual(@"np -n12 -c1 ""C:\w\a.cpp""", Expanded(EditorCommand.Template("np -n{line} -c{col} \"{file}\"", codeOnPath: true, location.Path)!));
        Assert.AreEqual(@"code -g ""C:\w\a.cpp:12""", Expanded(EditorCommand.Template(null, codeOnPath: true, location.Path)!));
        Assert.AreEqual(EditorCommand.Notepad, EditorCommand.Template(" ", codeOnPath: false, @"C:\w\run.bat"));
        Assert.IsNull(EditorCommand.Template(" ", codeOnPath: false, location.Path));
    }

    [TestMethod]
    public void TheExpandedCommandReadsThePathFromTheEnvironment()
    {
        var location = new SourceLocation(@"C:\w\a&calc.cpp", 1, 1);

        Assert.AreEqual("edit \"!BP_FILE!\"", EditorCommand.Expand("edit \"{file}\"", location));
        Assert.AreEqual(location.Path, EditorCommand.Environment(location)["BP_FILE"]);
    }
}
