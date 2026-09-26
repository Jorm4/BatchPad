using BatchPad.Core.Detection;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class ProposalTrackerTests
{
    [TestMethod]
    public void OnlyOptionsMissingFromTheEntryAreProposed()
    {
        using var temp = new TempDir();
        var script = temp.Path("tool.bat");
        File.WriteAllText(script, "@echo off\r\nrem   tool.bat [--release] [--gated]\r\necho %*\r\n");
        var entry = new ScriptNode { Path = "tool.bat", Params = [new ParameterDefinition { Name = "release", Type = ParameterType.Flag, Arg = "--release" }] };

        var proposals = ProposalTracker.For(entry, script, []);

        Assert.AreEqual("--gated", proposals.Parameters.Single().Arg);
        Assert.AreEqual("1 new option: --gated", proposals.Badge);
    }

    [TestMethod]
    public void DismissedProposalsAreLeftOut()
    {
        using var temp = new TempDir();
        var script = temp.Path("tool.bat");
        File.WriteAllText(script, "@echo off\r\nrem   tool.bat [--gated]\r\n");

        var proposals = ProposalTracker.For(new ScriptNode { Path = "tool.bat" }, script, ["param:--gated"]);

        Assert.IsTrue(proposals.IsEmpty);
        Assert.IsNull(proposals.Badge);
    }

    [TestMethod]
    public void ServeAndStopPairWithASharedParameterProposesStopAndLongRunning()
    {
        using var temp = new TempDir();
        var serve = temp.Path("serve_web.bat");
        File.WriteAllText(serve, "@echo off\r\npython -m http.server %~1\r\n");
        File.WriteAllText(temp.Path("stop_web.bat"), "@echo off\r\necho stopping port %~1\r\n");

        var proposals = ProposalTracker.For(new ScriptNode { Path = "serve_web.bat" }, serve, []);

        Assert.AreEqual("stop_web.bat", proposals.StopCompanion);
        Assert.IsNotNull(proposals.LongRunningReason);
        var configured = ProposalTracker.For(new ScriptNode { Path = "serve_web.bat", Stop = "stop-web", LongRunning = true }, serve, []);
        Assert.IsNull(configured.StopCompanion);
        Assert.IsNull(configured.LongRunningReason);
    }

    [TestMethod]
    public void StopWithoutASharedParameterIsNotAPair()
    {
        using var temp = new TempDir();
        var serve = temp.Path("serve_web.bat");
        File.WriteAllText(serve, "@echo off\r\necho serving %~1\r\n");
        File.WriteAllText(temp.Path("stop_web.bat"), "@echo off\r\necho stopping\r\n");

        Assert.IsNull(ProposalTracker.StopCompanionFor(serve));
    }

    [TestMethod]
    public void PythonServeAndStopPairIsFoundThroughTheProbes()
    {
        using var temp = new TempDir();
        const string script = """
            import argparse
            parser = argparse.ArgumentParser()
            parser.add_argument('--port', type=int)
            """;
        var serve = temp.Path("serve_web.py");
        File.WriteAllText(serve, script);
        File.WriteAllText(temp.Path("stop_web.py"), script);
        var probes = new ScriptProbes(new TrustStore(new Settings { TrustedFolders = [temp.Root] }, temp.Path("settings.json")), new InterpreterLocator());

        Assert.AreEqual("stop_web.py", ProposalTracker.For(new ScriptNode { Path = "serve_web.py" }, serve, [], probes).StopCompanion);
    }

    [TestMethod]
    public void DemoStopScriptAndPassThroughArgumentsProposeNothing()
    {
        var demo = Fixtures.DemoWorkspace;

        Assert.IsTrue(ProposalTracker.For(new ScriptNode { Path = "stop_serve.py" }, Path.Combine(demo, "stop_serve.py"), []).IsEmpty);
        Assert.IsTrue(ProposalTracker.For(new ScriptNode { Path = "hello.bat" }, Path.Combine(demo, "hello.bat"), []).IsEmpty);
        Assert.IsNotNull(Detector.Detect("serve.py", "").LongRunningReason);
        Assert.IsNull(Detector.Detect("stop-server.bat", "echo Press Ctrl+C").LongRunningReason);
    }
}
