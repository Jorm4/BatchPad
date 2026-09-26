using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Workspace;
using Microsoft.Extensions.Time.Testing;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class HistoryTests
{
    [TestMethod]
    public async Task AFailedRunIsRecordedWithItsLogAndSecretsMasked()
    {
        using var workspace = new RunWorkspace("""
            { "id": "fails", "path": "exit3.bat", "params": [ { "name": "token", "type": "secret" }, { "name": "mode", "type": "text" } ] }
            """, "exit3.bat");
        var store = new HistoryStore(workspace.Temp.Path("history"));
        var request = workspace.Request("fails", new() { ["token"] = "hunter2", ["mode"] = "fast" }) with { ExtraArguments = "--key hunter2" };
        using var run = workspace.Gate.Start(request, RunWorkspace.Interpreters);

        var record = await HistoryRecorder.Attach(run, store, request, "Workspace:id:fails").WaitAsync(Limit);

        Assert.AreEqual(3, record.ExitCode);
        Assert.AreEqual(RunOutcome.Exited, record.Outcome);
        Assert.AreEqual(RunTriggers.Manual, record.Trigger);
        Assert.AreEqual(RunRecord.Masked, record.Values["token"]!.GetValue<string>());
        Assert.AreEqual("fast", record.Values["mode"]!.GetValue<string>());
        Assert.DoesNotContain("hunter2", record.Command);
        Assert.AreEqual($"--key {RunRecord.Masked}", record.ExtraArguments);
        var log = File.ReadAllText(store.LogPath(record));
        StringAssert.Contains(log, $"{RunRecord.Masked} fast");
        Assert.DoesNotContain("hunter2", log);
        Assert.DoesNotContain("hunter2", File.ReadAllText(Path.Combine(store.Directory, record.Id + ".json")));
    }

    [TestMethod]
    public void ASecretDefaultFromTheEnvironmentIsMasked()
    {
        Environment.SetEnvironmentVariable("BP_MASK_TEST_TOKEN", "from-env-4711");
        using var workspace = new RunWorkspace("""
            { "id": "deploy", "path": "exit3.bat", "params": [ { "name": "token", "type": "secret", "default": "${env:BP_MASK_TEST_TOKEN}" } ] }
            """, "exit3.bat");

        var secrets = SecretMasker.SecretValues(workspace.Request("deploy"));

        CollectionAssert.AreEqual(new[] { "from-env-4711" }, secrets.ToArray());
    }

    [TestMethod]
    public void PruningKeepsTheNewestRecords()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var store = new HistoryStore(dir.Root, time, maxRecords: 3);

        for (var i = 0; i < 5; i++)
            store.Add(Record($"run{i}", time.GetUtcNow().AddMinutes(i)), [$"line {i}"]);

        CollectionAssert.AreEqual(new[] { "run4", "run3", "run2" }, store.Recent().Select(r => r.Name).ToArray());
        Assert.HasCount(6, Directory.GetFiles(dir.Root));
    }

    [TestMethod]
    public void RecordsAddedOutOfOrderAreListedNewestFirst()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var store = new HistoryStore(dir.Root, time);

        foreach (var minute in new[] { 2, 0, 3, 1 })
            store.Add(Record($"run{minute}", time.GetUtcNow().AddMinutes(minute)), []);

        CollectionAssert.AreEqual(new[] { "run3", "run2", "run1", "run0" }, store.Recent().Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public void AReadOnlyRecordThatCannotBePrunedDoesNotFailTheNextAdd()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var store = new HistoryStore(dir.Root, time, maxRecords: 1);
        var first = store.Add(Record("first", time.GetUtcNow()), []);
        var path = Path.Combine(dir.Root, first.Id + ".json");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            store.Add(Record("second", time.GetUtcNow().AddMinutes(1)), []);

            CollectionAssert.AreEqual(new[] { "second" }, store.Recent().Select(r => r.Name).ToArray());
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public void PruningDropsRecordsOlderThanTheMaximumAge()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var store = new HistoryStore(dir.Root, time);
        store.Add(Record("old", time.GetUtcNow()), []);

        time.Advance(HistoryStore.DefaultMaxAge + TimeSpan.FromDays(1));
        store.Add(Record("new", time.GetUtcNow()), []);

        CollectionAssert.AreEqual(new[] { "new" }, store.Recent().Select(r => r.Name).ToArray());
    }

    [TestMethod]
    public void ReadingNeverPrunesButTheNextAddDoes()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var writer = new HistoryStore(dir.Root, time);
        for (var i = 0; i < 3; i++)
            writer.Add(Record($"run{i}", time.GetUtcNow().AddMinutes(i)), []);
        var reader = new HistoryStore(dir.Root, time, maxRecords: 1);

        Assert.HasCount(3, reader.Recent());
        Assert.IsNotNull(reader.Find(r => r.Name == "run0"));
        Assert.HasCount(6, Directory.GetFiles(dir.Root));

        reader.Add(Record("run3", time.GetUtcNow().AddMinutes(3)), []);

        CollectionAssert.AreEqual(new[] { "run3" }, reader.Recent().Select(r => r.Name).ToArray());
        Assert.HasCount(2, Directory.GetFiles(dir.Root));
    }

    [TestMethod]
    public void ARecordThatWasLockedWhenFirstSeenIsReadOnTheNextLook()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var reader = new HistoryStore(dir.Root, time);
        Assert.IsEmpty(reader.Recent());
        var written = new HistoryStore(dir.Root, time).Add(Record("build", time.GetUtcNow()), []);

        using (new FileStream(Path.Combine(dir.Root, written.Id + ".json"), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.IsNull(reader.Find(written.Id));

        Assert.AreEqual("build", reader.Find(written.Id)?.Name);
    }

    [TestMethod]
    public void TheLastResultPerNodeSurvivesANewStore()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var store = new HistoryStore(dir.Root, time);
        store.Add(Record("build", time.GetUtcNow(), exitCode: 0), []);
        store.Add(Record("build", time.GetUtcNow().AddMinutes(1), exitCode: 3), []);
        store.Add(Record("build", time.GetUtcNow().AddMinutes(2), outcome: RunOutcome.Stopped), []);
        store.Add(Record("test", time.GetUtcNow().AddMinutes(3), exitCode: 0), []);

        var last = new HistoryStore(dir.Root, time).LastResults();

        Assert.AreEqual(3, last["Workspace:id:build"].ExitCode);
        Assert.IsTrue(last["Workspace:id:test"].Succeeded);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 1, 9, 1, 0, TimeSpan.Zero), last["Workspace:id:build"].StartedAt);
    }

    [TestMethod]
    public async Task RunRecordedFiresOncePerRun()
    {
        using var workspace = new RunWorkspace("""{ "id": "fails", "path": "exit3.bat" }""", "exit3.bat");
        var store = new HistoryStore(workspace.Temp.Path("history"));
        var recorded = new List<RunRecord>();
        store.RunRecorded += recorded.Add;

        foreach (var _ in Enumerable.Range(0, 2))
        {
            var request = workspace.Request("fails");
            using var run = workspace.Gate.Start(request, RunWorkspace.Interpreters);
            await HistoryRecorder.Attach(run, store, request, "Workspace:id:fails").WaitAsync(Limit);
        }

        Assert.HasCount(2, recorded);
        Assert.AreNotEqual(recorded[0].Id, recorded[1].Id);
    }

    private static RunRecord Record(string name, DateTimeOffset startedAt, int exitCode = 0, RunOutcome outcome = RunOutcome.Exited) => new()
    {
        NodeKey = $"{TreeKind.Workspace}:id:{name}",
        Name = name,
        StartedAt = startedAt,
        ExitCode = exitCode,
        Outcome = outcome,
        Values = new() { ["n"] = JsonValue.Create(1) },
    };
}
