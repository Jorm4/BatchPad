using System.Diagnostics;
using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Telemetry;
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
