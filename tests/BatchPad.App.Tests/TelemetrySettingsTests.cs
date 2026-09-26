using BatchPad.App.ViewModels.AppSettings;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class TelemetrySettingsTests
{
    [TestMethod]
    public void AddingAJsonlSinkWritesSettingsAndRoundTrips()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var file = Path.Combine(test.Root, "events", "runs.jsonl");

        main.OpenSettingsCommand.Execute(null);
        var telemetry = main.SettingsPage!.Telemetry;
        telemetry.NewSinkType = SinkTypes.Jsonl;
        telemetry.AddSinkCommand.Execute(null);
        telemetry.Sinks[0].Path = file;
        telemetry.Sinks[0].MaxSizeMb = "5";
        telemetry.User = true;

        var saved = Settings.Load(test.Paths.SettingsFile).Telemetry!;
        Assert.IsTrue(saved.User);
        var sink = saved.Sinks.Single();
        Assert.AreEqual(SinkTypes.Jsonl, sink.Type);
        Assert.AreEqual(file, sink.Path);
        Assert.AreEqual(5, sink.MaxSizeMb);

        main.EscapeCommand.Execute(null);
        Assert.IsNull(main.SettingsPage);

        var reopened = test.OpenMain(TestWorkspace.DemoSource, settings: Settings.Load(test.Paths.SettingsFile));
        reopened.OpenSettingsCommand.Execute(null);
        var loaded = reopened.SettingsPage!.Telemetry;
        Assert.IsTrue(loaded.User);
        Assert.AreEqual(file, loaded.Sinks.Single().Path);
        Assert.AreEqual("5", loaded.Sinks.Single().MaxSizeMb);
    }

    [TestMethod]
    public void TheMcpServerIsOffUntilTurnedOnInSettings()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        main.OpenSettingsCommand.Execute(null);
        Assert.IsFalse(main.SettingsPage!.McpEnabled);

        main.SettingsPage.McpEnabled = true;

        Assert.IsTrue(Settings.Load(test.Paths.SettingsFile).Mcp!.Enabled);
        var reopened = test.OpenMain(TestWorkspace.DemoSource, settings: Settings.Load(test.Paths.SettingsFile));
        reopened.OpenSettingsCommand.Execute(null);
        Assert.IsTrue(reopened.SettingsPage!.McpEnabled);
    }

    [TestMethod]
    public async Task SendTestEventWritesOneLineToAJsonlSink()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var file = Path.Combine(test.Root, "runs.jsonl");
        var sink = AddSink(main, SinkTypes.Jsonl);
        sink.Path = file;

        await sink.SendTestEventCommand.ExecuteAsync(null);

        Assert.AreEqual("Test event sent.", sink.TestResult);
        Assert.IsFalse(sink.TestFailed);
        var line = File.ReadAllLines(file).Single();
        StringAssert.Contains(line, $"\"{TelemetryPipeline.TestTrigger}\"");
        Assert.IsNotNull(sink.Status?.LastSuccess);
        Assert.IsFalse(sink.IsFailing);
    }

    [TestMethod]
    public async Task AFailedSendShowsTheErrorAsTheSinksStatus()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var sink = AddSink(main, SinkTypes.Jsonl);

        await sink.SendTestEventCommand.ExecuteAsync(null);

        Assert.IsTrue(sink.TestFailed);
        StringAssert.StartsWith(sink.TestResult, "Failed:");
        Assert.IsTrue(sink.IsFailing);
        StringAssert.Contains(sink.LastErrorText, "no path");
    }

    [TestMethod]
    public void AHeaderUsingAnEnvironmentReferenceIsStoredAsWritten()
    {
        var variable = "BATCHPAD_TEST_TOKEN_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(variable, "resolved-token-value");
        try
        {
            using var test = new TestWorkspace();
            var main = test.OpenMain(TestWorkspace.DemoSource);
            var sink = AddSink(main, SinkTypes.Otlp);
            sink.Endpoint = "https://collector.example.com:4318";
            sink.HeadersText = $"Authorization: Bearer ${{env:{variable}}}";

            Assert.IsNull(sink.SecretWarning);
            var saved = Settings.Load(test.Paths.SettingsFile).Telemetry!.Sinks.Single();
            Assert.AreEqual($"Bearer ${{env:{variable}}}", saved.Headers!["Authorization"]);
            Assert.DoesNotContain("resolved-token-value", File.ReadAllText(test.Paths.SettingsFile));

            sink.HeadersText = "Authorization: Bearer abc123";
            Assert.AreEqual(SinkViewModel.SecretWarningText, sink.SecretWarning);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [TestMethod]
    public void RemovingASinkSavesTheShorterList()
    {
        using var test = new TestWorkspace();
        var main = test.OpenMain(TestWorkspace.DemoSource);
        var sink = AddSink(main, SinkTypes.Influx);
        sink.Url = "https://influx.example.com";

        sink.RemoveCommand.Execute(null);

        Assert.IsFalse(main.SettingsPage!.Telemetry.HasSinks);
        Assert.IsEmpty(Settings.Load(test.Paths.SettingsFile).Telemetry!.Sinks);
    }

    private static SinkViewModel AddSink(ViewModels.MainViewModel main, string type)
    {
        main.OpenSettingsCommand.Execute(null);
        var telemetry = main.SettingsPage!.Telemetry;
        telemetry.NewSinkType = type;
        telemetry.AddSinkCommand.Execute(null);
        return telemetry.Sinks[^1];
    }
}
