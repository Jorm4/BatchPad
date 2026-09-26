using BatchPad.Core.Detection;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

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
        Assert.IsNotNull(Detect("serve_site.py").LongRunningReason);
        Assert.IsNotNull(Detector.Detect("run.bat", "echo Press Ctrl+C to stop.").LongRunningReason);
        Assert.IsNull(Detect("build.bat").LongRunningReason);
    }

    private static ScriptProbes Probes(bool trusted) =>
        new(new TrustStore(new Settings { TrustedFolders = trusted ? [Fixtures.Path("detect")] : [] }, Fixtures.Path("detect", "unused.json")),
            new InterpreterLocator());

    [TestMethod]
    public void ArgparseCallsBecomeParameters()
    {
        var parameters = Detector.Detect(Fixtures.Path("detect", "tool.py"), Probes(trusted: true)).Parameters;

        var jobs = parameters.Single(p => p.Arg == "--jobs");
        Assert.AreEqual(ParameterType.Int, jobs.Type);
        Assert.AreEqual(4, jobs.Default!.GetValue<long>());
        Assert.AreEqual("parallel jobs", jobs.Description);
        Assert.AreEqual(ParameterType.Flag, parameters.Single(p => p.Arg == "--gated").Type);
        var mode = parameters.Single(p => p.Arg == "--mode");
        Assert.AreEqual(ParameterType.Choice, mode.Type);
        CollectionAssert.AreEqual(new[] { "fast", "full" }, mode.Choices!.Select(c => c.Value).ToList());
        Assert.AreEqual("fast", mode.Default!.GetValue<string>());
        var inputs = parameters.Single(p => p.Name == "inputs");
        Assert.IsNull(inputs.Arg);
        Assert.AreEqual(ParameterType.Text, inputs.Type);
        Assert.IsTrue(inputs.Split);
        Assert.AreEqual("end", inputs.Position);
        Assert.HasCount(4, parameters);
    }

    [TestMethod]
    public void PowerShellParamBlockBecomesParameters()
    {
        var parameters = Detector.Detect(Fixtures.Path("detect", "tool.ps1"), Probes(trusted: true)).Parameters;

        var force = parameters.Single(p => p.Name == "Force");
        Assert.AreEqual(ParameterType.Flag, force.Type);
        Assert.AreEqual("-Force", force.Arg);
        var target = parameters.Single(p => p.Name == "Target");
        Assert.AreEqual(ParameterType.Choice, target.Type);
        CollectionAssert.AreEqual(new[] { "dev", "prod" }, target.Choices!.Select(c => c.Value).ToList());
        var retries = parameters.Single(p => p.Name == "Retries");
        Assert.AreEqual(ParameterType.Int, retries.Type);
        Assert.AreEqual(3, retries.Default!.GetValue<long>());
    }

    [TestMethod]
    public void UntrustedFolderDetectsNothingAndStartsNoProcess()
    {
        var probes = Probes(trusted: false);

        Assert.IsEmpty(Detector.Detect(Fixtures.Path("detect", "tool.py"), probes).Parameters);
        Assert.IsEmpty(Detector.Detect(Fixtures.Path("detect", "tool.ps1"), probes).Parameters);
        Assert.AreEqual(0, probes.ProcessesStarted);
    }

    [TestMethod]
    public void CachedDetectionFollowsFileChangesAndHandsOutCopies()
    {
        using var dir = new TempDir();
        var script = dir.Path("tool.bat");
        File.WriteAllText(script, "@echo off\r\nrem   tool.bat [--gated]\r\n");
        var first = Detector.Detect(script);
        first.Parameters.Clear();

        Assert.AreEqual("--gated", Detector.Detect(script).Parameters.Single().Arg);

        File.WriteAllText(script, "@echo off\r\nrem   tool.bat [--fast]\r\n");
        Assert.AreEqual("--fast", Detector.Detect(script).Parameters.Single().Arg);
    }

    [TestMethod]
    public void ProbesIgnoreModulesBesideTheScriptAndQuotesInItsPath()
    {
        using var dir = new TempDir();
        var folder = Directory.CreateDirectory(dir.Path("it’s here")).FullName;
        File.Copy(Fixtures.Path("detect", "tool.py"), Path.Combine(folder, "tool.py"));
        File.Copy(Fixtures.Path("detect", "tool.ps1"), Path.Combine(folder, "it’s tool.ps1"));
        File.WriteAllText(Path.Combine(folder, "json.py"), "raise SystemExit(3)");
        var probes = new ScriptProbes(new TrustStore(new Settings { TrustedFolders = [folder] }, dir.Path("settings.json")), new InterpreterLocator());

        Assert.HasCount(4, Detector.Detect(Path.Combine(folder, "tool.py"), probes).Parameters);
        Assert.IsTrue(Detector.Detect(Path.Combine(folder, "it’s tool.ps1"), probes).Parameters.Any(p => p.Name == "Force"));
    }
}
