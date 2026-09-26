using BatchPad.Core.Detection;
using BatchPad.Core.Model;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class DetectorTests
{
    private static DetectionResult Detect(string fileName) => Detector.Detect(Fixtures.Path("detect", fileName));

    [TestMethod]
    public void NameAndDescriptionFromBatchHeader()
    {
        var result = Detect("package_web.bat");

        Assert.AreEqual("Package web", result.Name);
        Assert.AreEqual("Packages a web build for deployment. Writes the zip next to the build.", result.Description);
    }

    [TestMethod]
    public void UsageHeaderProposesChoiceFlagAndPositionalList()
    {
        var result = Detect("build.bat");

        Assert.AreEqual("Builds the apps and the test projects.", result.Description);
        var choice = result.Parameters.Single(p => p.Type == ParameterType.Choice);
        CollectionAssert.AreEqual(new[] { "", "--release", "--final" }, choice.Choices!.Select(c => c.Value).ToList());

        var asan = result.Parameters.Single(p => p.Arg == "--asan");
        Assert.AreEqual(ParameterType.Flag, asan.Type);
        Assert.AreEqual("address sanitizer build", asan.Description);

        var list = result.Parameters.Single(p => p.Name == "name");
        Assert.AreEqual(ParameterType.Text, list.Type);
        Assert.IsTrue(list.Split);
        Assert.AreEqual("end", list.Position);

        var verbose = result.Parameters.Single(p => p.Arg == "--verbose");
        Assert.AreEqual(ParameterType.Flag, verbose.Type);
        Assert.HasCount(4, result.Parameters);
    }

    [TestMethod]
    public void ArgumentUseProposesPositionalText()
    {
        var result = Detect("copy_files.cmd");

        CollectionAssert.AreEqual(new[] { "arg1", "arg2" }, result.Parameters.Select(p => p.Name).ToList());
        Assert.IsTrue(result.Parameters.All(p => p.Type == ParameterType.Text));
        Assert.IsNull(result.Description);
    }

    [TestMethod]
    [DataRow("hello.py", "Says hello to everyone.")]
    [DataRow("regen.py", "Regenerates the asset index from the source folder.")]
    [DataRow("hello.ps1", "Cleans the temp folders.")]
    [DataRow("hello.cs", "Counts lines in a file.")]
    public void DescriptionPerCommentSyntax(string fileName, string description)
    {
        Assert.AreEqual(description, Detect(fileName).Description);
    }

    [TestMethod]
    public void LongRunningHints()
    {
        Assert.IsTrue(Detect("serve_site.py").LongRunning);
        Assert.IsTrue(Detector.Detect("run.bat", "echo Press Ctrl+C to stop.").LongRunning);
        Assert.IsFalse(Detect("build.bat").LongRunning);
    }
}
