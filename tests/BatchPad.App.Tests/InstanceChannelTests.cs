using System.Collections.Concurrent;
using System.IO.Pipes;
using BatchPad.App.Services;
using BatchPad.App.ViewModels;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class InstanceChannelTests
{
    [TestMethod]
    public async Task AServerReceivesARequestAndIgnoresAMalformedLine()
    {
        var name = $"BatchPad-Test-{Guid.NewGuid():N}";
        var received = new ConcurrentQueue<string>();
        using var channel = new InstanceChannel(name, received.Enqueue, _ => { });

        using (var raw = new NamedPipeClientStream(".", name, PipeDirection.Out))
        {
            raw.Connect(5000);
            using var writer = new StreamWriter(raw);
            writer.WriteLine("{\"run\": 42} not json");
        }
        await Eventually(() => InstanceChannel.TrySend(name, "workspace:hello"));

        await Eventually(() => !received.IsEmpty);
        Assert.AreEqual("workspace:hello", received.Single());
    }

    [TestMethod]
    public async Task ADuplicateKeyDoesNotStopTheServer()
    {
        var name = $"BatchPad-Test-{Guid.NewGuid():N}";
        var received = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<Exception>();
        using var channel = new InstanceChannel(name, received.Enqueue, errors.Enqueue);

        using (var raw = new NamedPipeClientStream(".", name, PipeDirection.Out))
        {
            raw.Connect(5000);
            using var writer = new StreamWriter(raw);
            writer.WriteLine("{\"run\":\"a\",\"run\":\"b\"}");
        }
        await Eventually(() => InstanceChannel.TrySend(name, "workspace:hello"));

        await Eventually(() => !received.IsEmpty);
        Assert.AreEqual("workspace:hello", received.Single());
        Assert.IsEmpty(errors);
    }

    [TestMethod]
    public async Task AnErrorHandlingOneRequestIsReportedAndTheServerKeepsServing()
    {
        var name = $"BatchPad-Test-{Guid.NewGuid():N}";
        var received = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<Exception>();
        using var channel = new InstanceChannel(name, key =>
        {
            if (key == "boom")
                throw new InvalidOperationException("boom");
            received.Enqueue(key);
        }, errors.Enqueue);

        await Eventually(() => InstanceChannel.TrySend(name, "boom"));
        await Eventually(() => !errors.IsEmpty);
        await Eventually(() => InstanceChannel.TrySend(name, "workspace:hello"));
        await Eventually(() => !received.IsEmpty);

        Assert.AreEqual("boom", errors.Single().Message);
        Assert.AreEqual("workspace:hello", received.Single());
    }

    [TestMethod]
    public async Task TheSwitchServesTheOpenWorkspaceAndDropsARequestForTheOneBefore()
    {
        var exe = Path.Combine(Path.GetTempPath(), $"BatchPad-Test-{Guid.NewGuid():N}.exe");
        var first = Path.Combine(Path.GetTempPath(), "first", "batchpad.json");
        var second = Path.Combine(Path.GetTempPath(), "second", "batchpad.json");
        string? open = first;
        var queued = new ConcurrentQueue<Action>();
        var ran = new List<string>();
        using var channels = new InstanceChannelSwitch(exe, () => open, queued.Enqueue, ran.Add, _ => { });
        channels.Update();
        Assert.AreEqual(InstanceChannel.PipeNameFor(exe, first), channels.PipeName);

        await Eventually(() => InstanceChannel.TrySend(channels.PipeName!, "workspace:hello"));
        await Eventually(() => !queued.IsEmpty);
        open = second;
        channels.Update();
        queued.Single()();

        Assert.IsEmpty(ran);
        Assert.AreEqual(InstanceChannel.PipeNameFor(exe, second), channels.PipeName);
        await Eventually(() => InstanceChannel.TrySend(channels.PipeName!, "workspace:other"));
        await Eventually(() => queued.Count == 2);
        queued.Last()();
        Assert.AreEqual("workspace:other", ran.Single());

        open = null;
        channels.Update();
        Assert.IsNull(channels.PipeName);
    }

    [TestMethod]
    public void NoServerMeansNoHandOff() => Assert.IsFalse(InstanceChannel.TrySend($"BatchPad-Test-{Guid.NewGuid():N}", "workspace:hello"));

    [TestMethod]
    [DataRow("{\"run\":\"workspace:hello\"}", "workspace:hello")]
    [DataRow("{\"run\":\"\"}", null)]
    [DataRow("{\"stop\":\"workspace:hello\"}", null)]
    [DataRow("[\"workspace:hello\"]", null)]
    [DataRow("run workspace:hello", null)]
    [DataRow("{\"run\":\"a\",\"run\":\"b\"}", null)]
    public void OnlyARunRequestIsAccepted(string line, string? key) => Assert.AreEqual(key, InstanceChannel.ParseRequest(line));

    [TestMethod]
    public void ThePipeNameDependsOnTheExeAndTheWorkspace()
    {
        var name = InstanceChannel.PipeNameFor(@"C:\Tools\BatchPad.exe", @"C:\Work\batchpad.json");
        Assert.AreEqual(name, InstanceChannel.PipeNameFor(@"c:\tools\batchpad.exe", @"C:\Work\.\BATCHPAD.json"));
        Assert.AreNotEqual(name, InstanceChannel.PipeNameFor(@"C:\Dev\BatchPad.exe", @"C:\Work\batchpad.json"));
        Assert.AreNotEqual(name, InstanceChannel.PipeNameFor(@"C:\Tools\BatchPad.exe", @"C:\Other\batchpad.json"));
    }

    [TestMethod]
    [DataRow(new[] { "--run", "workspace:hello", "--workspace", @"demo\batchpad.json" }, @"demo\batchpad.json", "workspace:hello")]
    [DataRow(new[] { @"demo\batchpad.json", "--run", "workspace:hello" }, @"demo\batchpad.json", "workspace:hello")]
    [DataRow(new[] { "demo" }, "demo", null)]
    [DataRow(new string[0], null, null)]
    public void TheCommandLineGivesTheWorkspaceAndTheNodeToRun(string[] args, string? workspace, string? runKey) =>
        Assert.AreEqual(new GuiArguments(workspace, runKey), GuiArguments.Parse(args));

    [TestMethod]
    public void RunByKeyStartsTheNode()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: launcher, shell: new FakeShell());
        var key = main.Tree!.Find("Workspace/Hello/hello.bat")!.Key;

        main.RunByKey(key);

        Assert.AreEqual("hello.bat", launcher.Requests.Single().Script.Name);
        Assert.AreEqual(key, main.Output.Tabs.Single().Node?.Key);
        launcher.Started.Single().Finish(RunOutcome.Exited, 0);
    }

    [TestMethod]
    public void RunByKeyReportsAnUnknownKey()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = test.OpenMain(TestWorkspace.DemoSource, trusted: true, launcher: launcher, shell: new FakeShell());

        main.RunByKey("workspace:missing");

        Assert.IsEmpty(launcher.Requests);
        var tab = (RunViewModel)main.Output.Tabs.Single();
        StringAssert.Contains(tab.Lines.Single().Text, "'workspace:missing'");
        Assert.AreEqual("failed to start", tab.StatusText);
    }

    [TestMethod]
    public void RunByKeyReportsThatKeptEditsStoppedTheRun()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = test.OpenMain(test.CopyDemo(), trusted: true, launcher: launcher, shell: new FakeShell(), confirm: new FakeConfirm { Answer = false });
        main.Select("Workspace/Parameters demo");
        main.Details.EditCommand.Execute(null);
        main.Details.Editor!.General.Name = "Renamed";

        main.RunByKey(main.Tree!.Find("Workspace/Hello/hello.bat")!.Key);

        Assert.IsEmpty(launcher.Requests);
        var tab = (RunViewModel)main.Output.Tabs.Single();
        StringAssert.Contains(tab.Lines.Single().Text, "Finish or discard your edits first");
        Assert.AreEqual("failed to start", tab.StatusText);
    }

    [TestMethod]
    public void RunByKeyRefusesInAnUntrustedWorkspace()
    {
        using var test = new TestWorkspace();
        var launcher = new FakeLauncher();
        var main = test.OpenMain(TestWorkspace.DemoSource, launcher: launcher, shell: new FakeShell());
        main.DismissTrustPromptCommand.Execute(null);

        main.RunByKey(main.Tree!.Find("Workspace/Hello/hello.bat")!.Key);

        Assert.IsEmpty(launcher.Requests);
        Assert.IsEmpty(main.Output.Tabs);
        Assert.IsTrue(main.IsTrustPromptVisible);
    }
}
