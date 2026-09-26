using System.Diagnostics;
using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workspace;
using Microsoft.Extensions.Time.Testing;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TelemetryPipelineTests
{
    private static readonly SinkConfig Fake = new() { Type = "fake", Url = "https://collector.example.com" };

    private static RunRecord Record(string name = "Build") =>
        TelemetryEventTests.SampleRecord() with { Id = "", Name = name, StartedAt = DateTimeOffset.Now };

    // Reads the way a log shipper does: shared with the writer, ignoring a line still being written.
    private static string[] CompleteLines(string path)
    {
        if (!File.Exists(path))
            return [];
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var text = new StreamReader(stream).ReadToEnd();
            var end = text.LastIndexOf('\n');
            return end < 0 ? [] : text[..end].Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static TelemetryPipeline Pipeline(TempDir dir, SinkConfig sink, ITelemetrySink? instance = null, TimeProvider? time = null) =>
        new(dir.Path("telemetry"), () => new TelemetryOptions { Sinks = [sink] }, time,
            instance is null ? null : _ => instance);

    [TestMethod]
    public async Task RecordsFlowToTheJsonlFileAsEvents()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(dir.Path("history"));
        await using var pipeline = Pipeline(dir, new SinkConfig { Type = SinkTypes.Jsonl, Path = dir.Path("out", "runs.jsonl") });
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);
        pipeline.Start();

        var first = store.Add(Record("One"), []);
        var second = store.Add(Record("Two"), []);

        await Eventually(() => CompleteLines(dir.Path("out", "runs.jsonl")).Length == 2);
        var events = CompleteLines(dir.Path("out", "runs.jsonl")).Select(l => JsonNode.Parse(l)!).ToList();
        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, events.Select(e => e["runId"]!.GetValue<string>()).ToList());
        Assert.AreEqual("One", events[0]["script"]!["name"]!.GetValue<string>());
        Assert.AreEqual("5c0f", events[0]["workspace"]!["id"]!.GetValue<string>());
        await Eventually(() => pipeline.Statuses().Single() is { Pending: 0, LastSuccess: not null });
    }

    [TestMethod]
    public async Task NothingIsQueuedWithoutSinks()
    {
        using var dir = new TempDir();
        var store = new HistoryStore(dir.Path("history"));
        await using var pipeline = new TelemetryPipeline(dir.Path("telemetry"), () => null);
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);

        store.Add(Record(), []);
        await pipeline.FlushAsync(TimeSpan.FromSeconds(1));

        Assert.IsFalse(Directory.Exists(dir.Path("telemetry")));
    }

    [TestMethod]
    public async Task AFailingSinkRetriesWithBackoffAndNoEventIsLost()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.Now);
        var sink = new FakeSink { Fail = attempt => attempt <= 2 ? new HttpRequestException("collector down") : null };
        var store = new HistoryStore(dir.Path("history"));
        await using var pipeline = Pipeline(dir, Fake, sink, time);
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);
        pipeline.Start();

        store.Add(Record("One"), []);
        await Eventually(() => sink.Attempts == 1);
        store.Add(Record("Two"), []);
        await Task.Delay(200);
        Assert.AreEqual(1, sink.Attempts);

        time.Advance(TelemetryPipeline.FirstRetry);
        await Eventually(() => sink.Attempts == 2);
        time.Advance(TelemetryPipeline.FirstRetry);
        await Task.Delay(200);
        Assert.AreEqual(2, sink.Attempts);

        time.Advance(TelemetryPipeline.FirstRetry);
        await Eventually(() => sink.Received.Count == 2);
        CollectionAssert.AreEqual(new[] { "One", "Two" }, sink.Received.Select(e => e.Script.Name).ToList());
        Assert.AreEqual(3, sink.Attempts);
        await Eventually(() => pipeline.Statuses().Single() is { Pending: 0, IsFailing: false });
    }

    [TestMethod]
    public async Task StatusReportsTheLastErrorAndWhatIsPending()
    {
        using var dir = new TempDir();
        var sink = new FakeSink { Fail = _ => new IOException("collector down") };
        var store = new HistoryStore(dir.Path("history"));
        await using var pipeline = Pipeline(dir, Fake, sink, new FakeTimeProvider(DateTimeOffset.Now));
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);
        pipeline.Start();

        store.Add(Record("One"), []);
        await Eventually(() => sink.Attempts == 1 && pipeline.Statuses().Single().IsFailing);
        store.Add(Record("Two"), []);

        var status = pipeline.Statuses().Single();
        Assert.AreEqual("collector down", status.LastError);
        Assert.AreEqual(2, status.Pending);
        Assert.AreEqual(Fake.Target, status.Target);
        StringAssert.Contains(File.ReadAllText(pipeline.StatusFile.Path), "collector down");
    }

    [TestMethod]
    public async Task ARejectedBatchIsDroppedNotRetried()
    {
        using var dir = new TempDir();
        var sink = new FakeSink { Fail = _ => new TelemetrySendException("400 Bad Request", retry: false) };
        var store = new HistoryStore(dir.Path("history"));
        await using var pipeline = Pipeline(dir, Fake, sink);
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);

        store.Add(Record(), []);
        await pipeline.FlushAsync(Limit);

        var status = pipeline.Statuses().Single();
        Assert.AreEqual(0, status.Pending);
        StringAssert.Contains(status.LastError, "400 Bad Request");
    }

    [TestMethod]
    public async Task ABatchSomeoneElseHasOpenWaitsInsteadOfBeingDropped()
    {
        using var dir = new TempDir();
        var sink = new FakeSink();
        var store = new HistoryStore(dir.Path("history"));
        await using var pipeline = Pipeline(dir, Fake, sink);
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);
        store.Add(Record(), []);

        using (new FileStream(pipeline.Outbox.Batches(Fake.Key).Single().File, FileMode.Open, FileAccess.Read, FileShare.Delete))
            await pipeline.FlushAsync(Limit);
        Assert.AreEqual(0, sink.Attempts);
        Assert.AreEqual(1, pipeline.Outbox.Pending(Fake.Key));

        await pipeline.FlushAsync(Limit);
        Assert.HasCount(1, sink.Received);
    }

    [TestMethod]
    public async Task ABatchSomeoneElseHasOpenHoldsUpOnlyItsOwnSink()
    {
        using var dir = new TempDir();
        var other = new SinkConfig { Type = "fake", Url = "https://other.example.com" };
        var (held, free) = (new FakeSink(), new FakeSink());
        await using var pipeline = new TelemetryPipeline(dir.Path("telemetry"), () => new TelemetryOptions { Sinks = [Fake, other] },
            createSink: c => c == Fake ? held : free);
        pipeline.Enqueue(Record(), TelemetryEventTests.Workspace);

        using (new FileStream(pipeline.Outbox.Batches(Fake.Key).Single().File, FileMode.Open, FileAccess.Read, FileShare.Delete))
            await pipeline.FlushAsync(Limit);

        Assert.AreEqual(0, held.Attempts);
        Assert.HasCount(1, free.Received);
        Assert.AreEqual(1, pipeline.Outbox.Pending(Fake.Key));
    }

    [TestMethod]
    public async Task ABacklogIsSentInFullBatches()
    {
        using var dir = new TempDir();
        var sink = new FakeSink();
        await using var pipeline = Pipeline(dir, Fake, sink);
        var telemetryEvent = TelemetryEvents.From(Record(), TelemetryEventTests.Workspace, new TelemetryOptions());
        for (var i = 0; i < 250; i++)
            pipeline.Outbox.Add(Fake.Key, [telemetryEvent with { RunId = $"run-{i}" }]);

        await pipeline.FlushAsync(Limit);

        Assert.AreEqual(3, sink.Attempts);
        CollectionAssert.AreEqual(Enumerable.Range(0, 250).Select(i => $"run-{i}").ToList(), sink.Received.Select(e => e.RunId).ToList());
        Assert.AreEqual(0, pipeline.Statuses().Single().Pending);
    }

    [TestMethod]
    public async Task WhatAnotherProcessQueuedIsDeliveredWhileIdle()
    {
        using var dir = new TempDir();
        var time = new FakeTimeProvider(DateTimeOffset.Now);
        var sink = new FakeSink();
        await using var pipeline = Pipeline(dir, Fake, sink, time);
        var firstPass = new TaskCompletionSource();
        pipeline.StatusChanged += () => firstPass.TrySetResult();
        pipeline.Start();
        await firstPass.Task.WaitAsync(Limit);

        pipeline.Outbox.Add(Fake.Key, [TelemetryEvents.From(Record("Elsewhere"), TelemetryEventTests.Workspace, new TelemetryOptions())]);
        await Task.Delay(200);
        Assert.AreEqual(0, sink.Attempts);

        var deadline = DateTime.UtcNow + Limit;
        while (sink.Received.Count == 0 && DateTime.UtcNow < deadline)
        {
            time.Advance(TelemetryPipeline.IdleRecheck);
            await Task.Delay(50);
        }
        Assert.AreEqual("Elsewhere", sink.Received.Single().Script.Name);
    }

    [TestMethod]
    public void NullSinksInSettingsReadAsNone()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("settings.json"), """{ "telemetry": { "sinks": null } }""");
        File.WriteAllText(dir.Path("other.json"), """{ "telemetry": { "sinks": [ null, { "type": "jsonl", "path": "runs.jsonl" } ] } }""");

        Assert.IsEmpty(Settings.Load(dir.Path("settings.json")).Telemetry!.Sinks);
        Assert.AreEqual(SinkTypes.Jsonl, Settings.Load(dir.Path("other.json")).Telemetry!.Sinks.Single().Type);
    }

    [TestMethod]
    public async Task BadTelemetrySettingsNeverFailARun()
    {
        using var dir = new TempDir();
        var path = System.Text.Json.JsonSerializer.Serialize(dir.Path("runs.jsonl"));
        File.WriteAllText(dir.Path("settings.json"), $$"""{ "telemetry": { "sinks": [ null, { "type": "jsonl", "path": {{path}} } ] } }""");
        var settings = Settings.Load(dir.Path("settings.json"));
        var store = new HistoryStore(dir.Path("history"));
        await using var pipeline = new TelemetryPipeline(dir.Path("telemetry"), () => settings.Telemetry);
        await using var broken = new TelemetryPipeline(dir.Path("broken"), () => throw new InvalidOperationException("unreadable settings"));
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);
        using var brokenSubscription = broken.Attach(store, TelemetryEventTests.Workspace);

        store.Add(Record(), []);
        await pipeline.FlushAsync(Limit);

        Assert.HasCount(1, File.ReadAllLines(dir.Path("runs.jsonl")));
        Assert.AreEqual(SinkTypes.Jsonl, pipeline.Statuses().Single().Type);
    }

    [TestMethod]
    public async Task AnUnreadableSaltIsNotRemembered()
    {
        using var dir = new TempDir();
        var salt = dir.Path("telemetry", "salt");
        Directory.CreateDirectory(salt);
        await using var pipeline = new TelemetryPipeline(dir.Path("telemetry"), () => new TelemetryOptions { HashNames = true, Sinks = [Fake] });

        pipeline.Enqueue(Record("Build"), TelemetryEventTests.Workspace);
        Directory.Delete(salt);
        File.WriteAllText(salt, "shared-salt");
        pipeline.Enqueue(Record("Build"), TelemetryEventTests.Workspace);

        var names = pipeline.Outbox.Batches(Fake.Key).Select(b => TelemetryOutbox.Read(b)!.Single().Script.Name).ToList();
        Assert.HasCount(2, names);
        Assert.AreNotEqual("Build", names[0]);
        Assert.AreEqual(TelemetryEvents.Hash("Build", "shared-salt"), names[1]);
    }

    [TestMethod]
    public void TheOutboxCleansUpAbandonedTemporaryFiles()
    {
        using var dir = new TempDir();
        var outbox = new TelemetryOutbox(dir.Path("outbox"));
        Directory.CreateDirectory(dir.Path("outbox", "sink"));
        var abandoned = dir.Path("outbox", "sink", "old.json.tmp");
        var inProgress = dir.Path("outbox", "sink", "new.json.tmp");
        File.WriteAllText(abandoned, "[");
        File.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddMinutes(-2));
        File.WriteAllText(inProgress, "[");

        outbox.Add("sink", [TelemetryEvents.From(TelemetryEventTests.SampleRecord(), TelemetryEventTests.Workspace, new TelemetryOptions())]);

        Assert.IsFalse(File.Exists(abandoned));
        Assert.IsTrue(File.Exists(inProgress));
        Assert.AreEqual(1, outbox.Pending("sink"));
    }

    [TestMethod]
    public void TheOutboxCapDropsTheOldestBatches()
    {
        using var dir = new TempDir();
        var events = Enumerable.Range(0, 10)
            .Select(i => TelemetryEvents.From(TelemetryEventTests.SampleRecord() with { Id = $"run-{i}" }, TelemetryEventTests.Workspace, new TelemetryOptions()))
            .ToList();
        var size = System.Text.Json.JsonSerializer.Serialize(new[] { events[0] }, TelemetryEvent.Json).Length;
        var outbox = new TelemetryOutbox(dir.Path("outbox"), maxBytes: size * 3 + size / 2);

        foreach (var telemetryEvent in events)
            outbox.Add("sink", [telemetryEvent]);

        var kept = outbox.Batches("sink").Select(b => TelemetryOutbox.Read(b)!.Single().RunId).ToList();
        CollectionAssert.AreEqual(new[] { "run-7", "run-8", "run-9" }, kept);
        Assert.AreEqual(3, outbox.Pending("sink"));
    }

    [TestMethod]
    public async Task ASlowSinkDoesNotDelayRecording()
    {
        using var dir = new TempDir();
        var sink = new FakeSink { Block = TimeSpan.FromSeconds(10) };
        var store = new HistoryStore(dir.Path("history"));
        var pipeline = Pipeline(dir, Fake, sink);
        using var subscription = pipeline.Attach(store, TelemetryEventTests.Workspace);
        var recorded = new List<string>();
        store.RunRecorded += record =>
        {
            lock (recorded)
                recorded.Add(record.Id);
        };
        pipeline.Start();
        store.Add(Record("One"), []);
        await Eventually(() => sink.Attempts == 1);

        var clock = Stopwatch.StartNew();
        var second = store.Add(Record("Two"), []);
        var elapsed = clock.Elapsed;

        Assert.IsLessThan(TimeSpan.FromSeconds(1), elapsed);
        lock (recorded)
            CollectionAssert.Contains(recorded, second.Id);
        Assert.AreEqual(2, pipeline.Outbox.Pending(Fake.Key));
        clock.Restart();
        await pipeline.DisposeAsync();
        Assert.IsLessThan(TimeSpan.FromSeconds(5), clock.Elapsed);
    }

    [TestMethod]
    public async Task ATestEventGoesStraightToTheSink()
    {
        using var dir = new TempDir();
        var path = dir.Path("runs.jsonl");
        await using var pipeline = new TelemetryPipeline(dir.Path("telemetry"), () => null);

        var error = await pipeline.SendTestEventAsync(new SinkConfig { Type = SinkTypes.Jsonl, Path = path });

        Assert.IsNull(error);
        var line = JsonNode.Parse(File.ReadAllLines(path).Single())!;
        Assert.AreEqual(TelemetryPipeline.TestTrigger, line["trigger"]!.GetValue<string>());
        Assert.IsNotNull(await pipeline.SendTestEventAsync(new SinkConfig { Type = "nope" }));
    }

    [TestMethod]
    public async Task TheJsonlFileRotatesAtItsSizeLimit()
    {
        using var dir = new TempDir();
        var path = dir.Path("runs.jsonl");
        File.WriteAllText(path, new string('x', 1024 * 1024));
        var sink = new JsonlSink(new SinkConfig { Type = SinkTypes.Jsonl, Path = path, MaxSizeMb = 1 });

        await sink.SendAsync([TelemetryEvents.From(TelemetryEventTests.SampleRecord(), TelemetryEventTests.Workspace, new TelemetryOptions())], default);

        Assert.AreEqual(1024 * 1024, new FileInfo(dir.Path("runs.1.jsonl")).Length);
        Assert.HasCount(1, File.ReadAllLines(path));
    }

    private sealed class FakeSink : ITelemetrySink
    {
        private int _attempts;

        public Func<int, Exception?>? Fail { get; init; }
        public TimeSpan Block { get; init; }
        public int Attempts => Volatile.Read(ref _attempts);

        public List<TelemetryEvent> Received
        {
            get
            {
                lock (_received)
                    return [.. _received];
            }
        }

        private readonly List<TelemetryEvent> _received = [];

        public async Task SendAsync(IReadOnlyList<TelemetryEvent> batch, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            if (Block > TimeSpan.Zero)
                await Task.Delay(Block, cancellationToken);
            if (Fail?.Invoke(attempt) is { } failure)
                throw failure;
            lock (_received)
                _received.AddRange(batch);
        }
    }
}
