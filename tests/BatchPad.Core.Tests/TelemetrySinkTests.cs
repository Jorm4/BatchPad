using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BatchPad.Core.History;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class TelemetrySinkTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 27, 9, 12, 3, 412, TimeSpan.Zero);
    private static readonly TelemetryWorkspace Workspace = new("5c0f", "My Project");

    /// <summary>A two-step workflow: its steps first, then the workflow's own record, as history saves them.</summary>
    private static List<TelemetryEvent> Workflow()
    {
        var workflow = new RunRecord
        {
            Id = "wf-run", NodeKey = "Workspace:id:release", Tree = TreeKind.Workspace, NodeId = "release", Name = "Release", Folder = "Ship",
            StartedAt = Start, Duration = TimeSpan.FromMilliseconds(5000), Machine = "WS-042", BatchPadVersion = "0.2.0",
            Git = new GitInfo("main", "3f9a1c2"),
        };
        var build = workflow with
        {
            Id = "step-1", ParentRunId = "wf-run", StepId = "build", NodeKey = "Workspace:id:build", NodeId = "build", Name = "Build",
            Path = "build.bat", Command = "build.bat", Folder = "Build", Tags = ["native"], StartedAt = Start.AddMilliseconds(100),
            Duration = TimeSpan.FromMilliseconds(3000), QueuedMs = 850, Values = new() { ["config"] = "Release" },
        };
        var test = build with
        {
            Id = "step-2", StepId = "test", NodeId = "unit", Name = "Unit tests", Folder = "Test, unit=fast", Tags = null,
            StartedAt = Start.AddMilliseconds(3200), Duration = TimeSpan.FromMilliseconds(1500), QueuedMs = 0, ExitCode = 3,
            Tests = new TestSummary(10, 1, 2, [], []), Values = [],
        };
        var options = new TelemetryOptions { IncludeValues = true };
        return [.. new[] { build, test, workflow with { Outcome = RunOutcome.Exited, ExitCode = 1 } }
            .Select(r => TelemetryEvents.From(r, Workspace, options))];
    }

    [TestMethod]
    public async Task OtlpSendsAWorkflowAsOneTraceWithItsStepsAsChildSpans()
    {
        Environment.SetEnvironmentVariable("BP_OTLP_TEST_TOKEN", "Bearer otlp-4711");
        var handler = new FakeHandler();
        using var sink = new OtlpSink(new SinkConfig
        {
            Type = SinkTypes.Otlp, Endpoint = "http://collector.example.com:4318", Headers = new() { ["Authorization"] = "${env:BP_OTLP_TEST_TOKEN}" },
        }, handler);

        await sink.SendAsync(Workflow(), default);

        var sent = handler.Sent.Single();
        Assert.AreEqual("http://collector.example.com:4318/v1/traces", sent.Url);
        Assert.AreEqual("Bearer otlp-4711", sent.Headers["Authorization"]);
        StringAssert.StartsWith(sent.Headers["Content-Type"], "application/json");
        GoldenJson("otlp.json", sent.Body);

        var spans = JsonNode.Parse(sent.Body)!["resourceSpans"]![0]!["scopeSpans"]![0]!["spans"]!.AsArray();
        var workflow = spans.Single(s => s!["name"]!.GetValue<string>() == "Release")!;
        Assert.AreEqual(1, spans.Select(s => s!["traceId"]!.GetValue<string>()).Distinct().Count());
        Assert.IsNull(workflow["parentSpanId"]);
        foreach (var step in spans.Where(s => s != workflow))
            Assert.AreEqual(workflow["spanId"]!.GetValue<string>(), step!["parentSpanId"]!.GetValue<string>());
        Assert.AreEqual(32, workflow["traceId"]!.GetValue<string>().Length);
        Assert.AreEqual(16, workflow["spanId"]!.GetValue<string>().Length);
    }

    [TestMethod]
    public async Task ElasticPostsBulkNdjsonWithAnApiKey()
    {
        var handler = new FakeHandler();
        using var sink = new ElasticSink(new SinkConfig { Type = SinkTypes.Elastic, Url = "https://es.example.com:9200/", Index = "runs", ApiKey = "a2V5" }, handler);

        await sink.SendAsync(Workflow(), default);

        var sent = handler.Sent.Single();
        Assert.AreEqual("https://es.example.com:9200/_bulk", sent.Url);
        Assert.AreEqual("ApiKey a2V5", sent.Headers["Authorization"]);
        StringAssert.StartsWith(sent.Headers["Content-Type"], "application/x-ndjson");
        GoldenText("elastic.ndjson", sent.Body);
    }

    [TestMethod]
    public async Task ElasticUsesBasicAuthWithoutAnApiKey()
    {
        var handler = new FakeHandler();
        using var sink = new ElasticSink(new SinkConfig { Type = SinkTypes.Elastic, Url = "https://es.example.com:9200", Username = "elastic", Password = "pw" }, handler);

        await sink.SendAsync(Workflow(), default);

        Assert.AreEqual("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("elastic:pw")), handler.Sent.Single().Headers["Authorization"]);
        StringAssert.Contains(handler.Sent.Single().Body, $"\"_index\":\"{ElasticSink.DefaultIndex}\"");
    }

    [TestMethod]
    public async Task ElasticErrorsInAnOkAnswerAreFailures()
    {
        const string Rejected = """{ "errors": true, "items": [ { "index": { "status": 400, "error": { "type": "mapper_parsing_exception", "reason": "bad field" } } } ] }""";
        const string Overloaded = """{ "errors": true, "items": [ { "index": { "status": 201 } }, { "index": { "status": 429, "error": { "type": "es_rejected_execution_exception" } } } ] }""";
        var config = new SinkConfig { Type = SinkTypes.Elastic, Url = "https://es.example.com:9200" };

        using var rejecting = new ElasticSink(config, new FakeHandler(_ => Answer(HttpStatusCode.OK, Rejected)));
        var rejected = await Assert.ThrowsAsync<TelemetrySendException>(() => rejecting.SendAsync(Workflow(), default));
        using var overloaded = new ElasticSink(config, new FakeHandler(_ => Answer(HttpStatusCode.OK, Overloaded)));
        var busy = await Assert.ThrowsAsync<TelemetrySendException>(() => overloaded.SendAsync(Workflow(), default));

        Assert.IsFalse(rejected.Retry);
        StringAssert.Contains(rejected.Message, "bad field");
        Assert.IsTrue(busy.Retry);
    }

    [TestMethod]
    public async Task InfluxWritesEscapedLineProtocolWithAToken()
    {
        var handler = new FakeHandler(_ => Answer(HttpStatusCode.NoContent, ""));
        using var sink = new InfluxSink(new SinkConfig { Type = SinkTypes.Influx, Url = "http://influx.example.com:8086", Org = "dev team", Bucket = "batchpad", Token = "t0k" }, handler);

        await sink.SendAsync(Workflow(), default);

        var sent = handler.Sent.Single();
        Assert.AreEqual("http://influx.example.com:8086/api/v2/write?org=dev%20team&bucket=batchpad&precision=ns", sent.Url);
        Assert.AreEqual("Token t0k", sent.Headers["Authorization"]);
        StringAssert.StartsWith(sent.Headers["Content-Type"], "text/plain");
        GoldenText("influx.txt", sent.Body);
        Assert.AreEqual(@"a\ b\,c\=d", InfluxSink.EscapeTag("a b,c=d"));
    }

    [TestMethod]
    public async Task HttpPostsAJsonArrayWithCustomHeaders()
    {
        var handler = new FakeHandler();
        using var sink = new HttpSink(new SinkConfig { Type = SinkTypes.Http, Url = "https://hooks.example.com/runs?source=bp", Headers = new() { ["X-Api-Key"] = "k1" } }, handler);

        await sink.SendAsync(Workflow(), default);

        var sent = handler.Sent.Single();
        Assert.AreEqual("https://hooks.example.com/runs?source=bp", sent.Url);
        Assert.AreEqual("k1", sent.Headers["X-Api-Key"]);
        var events = JsonNode.Parse(sent.Body)!.AsArray();
        CollectionAssert.AreEqual(new[] { "step-1", "step-2", "wf-run" }, events.Select(e => e!["runId"]!.GetValue<string>()).ToList());
    }

    [TestMethod]
    [DataRow(500, true)]
    [DataRow(503, true)]
    [DataRow(429, true)]
    [DataRow(408, true)]
    [DataRow(401, true)]
    [DataRow(403, true)]
    [DataRow(400, false)]
    [DataRow(404, false)]
    public async Task ServerErrorsThrottlingAndAuthFailuresAreRetriedOtherClientErrorsAreNot(int status, bool retry)
    {
        foreach (var type in SinkTypes.All.Where(t => t != SinkTypes.Jsonl))
        {
            var config = new SinkConfig { Type = type, Url = "https://sink.example.com", Endpoint = "https://sink.example.com", Org = "o", Bucket = "b" };
            using var sink = (HttpSinkBase)SinkFactory.Create(config, new FakeHandler(_ => Answer((HttpStatusCode)status, "nope")));

            var failure = await Assert.ThrowsAsync<TelemetrySendException>(() => sink.SendAsync(Workflow(), default));

            Assert.AreEqual(retry, failure.Retry, type);
            StringAssert.StartsWith(failure.Message, status.ToString(System.Globalization.CultureInfo.InvariantCulture), type);
        }
    }

    [TestMethod]
    public async Task OnlyHttpUrlsAreAllowed()
    {
        var handler = new FakeHandler();
        using var sink = new HttpSink(new SinkConfig { Type = SinkTypes.Http, Url = "file:///C:/Windows/win.ini" }, handler);

        var failure = await Assert.ThrowsAsync<TelemetrySendException>(() => sink.SendAsync(Workflow(), default));

        Assert.IsFalse(failure.Retry);
        Assert.IsEmpty(handler.Sent);
    }

    [TestMethod]
    public async Task ARedirectIsARetryableFailureNotFollowed()
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var answering = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            context.Response.StatusCode = (int)HttpStatusCode.Found;
            context.Response.RedirectLocation = "http://localhost:1/elsewhere";
            context.Response.Close();
        });
        using var sink = new HttpSink(new SinkConfig { Type = SinkTypes.Http, Url = $"http://localhost:{port}/runs", Headers = new() { ["X-Api-Key"] = "k1" } });

        var failure = await Assert.ThrowsAsync<TelemetrySendException>(() => sink.SendAsync(Workflow(), default));

        await answering;
        Assert.IsTrue(failure.Retry);
        StringAssert.Contains(failure.Message, "redirected to http://localhost:1/elsewhere");
    }

    [TestMethod]
    public async Task AnUnsetEnvReferenceKeepsTheEventsQueued()
    {
        using var dir = new TempDir();
        var handler = new FakeHandler();
        var config = new SinkConfig { Type = SinkTypes.Http, Url = "https://hooks.example.com/runs", Headers = new() { ["X-Api-Key"] = "${env:BP_TELEMETRY_UNSET_KEY}" } };
        await using var pipeline = new TelemetryPipeline(dir.Path("telemetry"), () => new TelemetryOptions { Sinks = [config] },
            createSink: c => SinkFactory.Create(c, handler));
        pipeline.Enqueue(TelemetryEventTests.SampleRecord(), Workspace);

        await pipeline.FlushAsync(Limit);

        var status = pipeline.Statuses().Single();
        Assert.AreEqual("Environment variable BP_TELEMETRY_UNSET_KEY is not set.", status.LastError);
        Assert.AreEqual(1, status.Pending);
        Assert.IsEmpty(handler.Sent);
    }

    [TestMethod]
    public async Task ThePipelineDropsARejectedBatchAndKeepsOneTheServerFailed()
    {
        using var dir = new TempDir();
        var status = HttpStatusCode.BadRequest;
        var handler = new FakeHandler(_ => Answer(status, "bad"));
        var config = new SinkConfig { Type = SinkTypes.Http, Url = "https://hooks.example.com/runs" };
        await using var pipeline = new TelemetryPipeline(dir.Path("telemetry"), () => new TelemetryOptions { Sinks = [config] },
            createSink: c => SinkFactory.Create(c, handler));
        var record = TelemetryEventTests.SampleRecord() with { Id = "" };
        var store = new HistoryStore(dir.Path("history"));
        using var subscription = pipeline.Attach(store, Workspace);

        store.Add(record with { StartedAt = DateTimeOffset.Now }, []);
        await pipeline.FlushAsync(Limit);
        var afterRejection = pipeline.Statuses().Single();
        status = HttpStatusCode.ServiceUnavailable;
        store.Add(record with { StartedAt = DateTimeOffset.Now }, []);
        await pipeline.FlushAsync(Limit);
        var afterOutage = pipeline.Statuses().Single();

        Assert.AreEqual(0, afterRejection.Pending);
        StringAssert.Contains(afterRejection.LastError, "400");
        Assert.AreEqual(1, afterOutage.Pending);
        StringAssert.Contains(afterOutage.LastError, "503");
        Assert.HasCount(2, handler.Sent);
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static void GoldenJson(string name, string actual)
    {
        var expected = File.ReadAllText(Fixtures.Path("telemetry", name));
        if (!JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)))
            Fail(name, JsonNode.Parse(actual)!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void GoldenText(string name, string actual)
    {
        if (File.ReadAllText(Fixtures.Path("telemetry", name)).Replace("\r\n", "\n") != actual)
            Fail(name, actual);
    }

    private static void Fail(string name, string actual)
    {
        var path = Path.Combine(Path.GetTempPath(), "BatchPadTests", "actual-" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, actual);
        Assert.Fail($"{name} differs from the golden file; the actual payload is in {path}");
    }

    private sealed record Sent(string Url, Dictionary<string, string> Headers, string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) : HttpMessageHandler
    {
        private readonly List<Sent> _sent = [];

        public List<Sent> Sent
        {
            get
            {
                lock (_sent)
                    return [.. _sent];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_sent)
                _sent.Add(new Sent(request.RequestUri!.AbsoluteUri, headers, body));
            return respond?.Invoke(request) ?? Answer(HttpStatusCode.OK, "{}");
        }
    }
}
