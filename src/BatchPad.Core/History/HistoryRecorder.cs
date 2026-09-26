using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using BatchPad.Core.Scheduling;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.History;

/// <summary>The one hook that saves a run to history when it finishes, whoever started it.</summary>
public static class HistoryRecorder
{
    /// <summary>Records <paramref name="run"/> as <paramref name="request"/>'s script, with its secret values masked.</summary>
    public static Task<RunRecord> Attach(IRunOutput run, HistoryStore store, RunRequest request, string nodeKey,
        string trigger = RunTriggers.Manual, string? name = null) =>
        Attach(run, store, request, nodeKey, trigger, name, parentRunId: null, stepId: null);

    /// <summary>Records <paramref name="run"/> from <paramref name="template"/>; its start, command and result are filled in.</summary>
    public static Task<RunRecord> Attach(IRunOutput run, HistoryStore store, RunRecord template,
        IReadOnlyCollection<string>? secrets = null)
    {
        if (run is WorkflowRunOutput { Run: var workflow })
            template = Describe(template with { Id = workflow.RunId }, workflow.Request.Tree, workflow.Workflow, workflow.Request.Tree.BaseDirectory);
        return Attach(run, store, template, secrets, request: null);
    }

    private static Task<RunRecord> Attach(IRunOutput run, HistoryStore store, RunRequest request, string nodeKey,
        string trigger, string? name, string? parentRunId, string? stepId)
    {
        var script = request.Script;
        var secretNames = SecretMasker.SecretNames(request);
        var secrets = SecretMasker.SecretValues(request);
        var values = (request.Values ?? new Dictionary<string, JsonNode?>()).ToDictionary(
            v => v.Key, v => secretNames.Contains(v.Key) ? RunRecord.Masked : v.Value?.DeepClone());
        var template = new RunRecord
        {
            Id = run is WorkflowRunOutput { Run.RunId: var runId } ? runId : "",
            NodeKey = nodeKey,
            Tree = request.Tree.Kind,
            NodeId = script.Id,
            Path = script.Path,
            Name = name ?? ScriptTree.DisplayName(script),
            Values = values,
            ExtraArguments = request.ExtraArguments is { } extra ? SecretMasker.Mask(extra, secrets) : null,
            Trigger = trigger,
            ParentRunId = parentRunId,
            StepId = stepId,
        };
        return Attach(run, store, Describe(template, request.Tree, script, request.Workspace.Directory), secrets, request);
    }

    private static async Task<RunRecord> Attach(IRunOutput run, HistoryStore store, RunRecord template,
        IReadOnlyCollection<string>? secrets, RunRequest? request)
    {
        var handle = run.Handle;
        var attachedAt = store.Time.GetLocalNow();
        var command = handle is null ? template.Command : string.Join(Environment.NewLine, handle.Specs.Select(s => s.Command.Display));
        var collected = new List<OutputLine>();
        RunResult result;
        using (handle is null ? run.Subscribe(line =>
               {
                   lock (collected)
                       collected.Add(line);
               }) : null)
            result = await run.Completion.ConfigureAwait(false);
        var startedAt = handle is { StartedAt: var started } && started != default ? started : attachedAt;

        IReadOnlyList<OutputLine> output;
        lock (collected)
            output = handle?.Output ?? [.. collected];
        return store.Add(template with
        {
            Command = Mask(command, secrets),
            StartedAt = startedAt,
            Duration = result.Duration,
            Outcome = result.Outcome,
            ExitCode = result.ExitCode,
            QueuedMs = (long)(handle?.Queued.TotalMilliseconds ?? 0),
            Tests = request is null ? null : Summarize(TestReportReader.ForFinishedRun(request, startedAt)),
            Errors = request is null ? null : ErrorsIn(output, request, secrets),
        }, output.Select(l => Mask(l.Text, secrets)));
    }

    private static RunRecord Describe(RunRecord record, ScriptTree tree, RunnableNode node, string directory)
    {
        var checkout = Checkout.Read(directory);
        return record with
        {
            Folder = tree.FolderOf(node),
            Tags = node.Tags is { Count: > 0 } tags ? [.. tags] : null,
            Git = checkout is { Branch: var branch, Commit: var commit } && (branch ?? commit) is not null ? new GitInfo(branch, commit) : null,
            Checkout = checkout,
        };
    }

    private static TestSummary? Summarize(JUnitReport? report)
    {
        if (report is null)
            return null;
        var cases = report.Cases.ToList();
        return new TestSummary(report.Count(TestOutcome.Passed), report.Count(TestOutcome.Failed), report.Count(TestOutcome.Skipped),
            [.. cases.Where(c => c.Outcome == TestOutcome.Failed).Select(QualifiedName).Take(TestSummary.MaxFailedNames)],
            [.. cases.Where(c => c.Outcome != TestOutcome.Skipped).OrderByDescending(c => c.Seconds).Take(TestSummary.MaxSlowest)
                .Select(c => new TestTiming(QualifiedName(c), c.Seconds))]);
    }

    private static string QualifiedName(TestCaseResult test) => Shorten(test.ClassName.Length > 0 ? $"{test.ClassName}.{test.Name}" : test.Name);

    private static string Shorten(string text) => text.Length > RunRecord.MaxTextLength ? text[..RunRecord.MaxTextLength] + "…" : text;

    private static List<ErrorLine>? ErrorsIn(IReadOnlyList<OutputLine> output, RunRequest request, IReadOnlyCollection<string>? secrets)
    {
        var parser = new OutputLineParser(request.Script.ErrorPatterns);
        var resolver = new SourceLocationResolver(() => RunPlanner.WorkingDirectoryFor(request));
        var errors = new List<ErrorLine>();
        foreach (var line in output)
        {
            if (line.Stream == OutputStream.Info)
                continue;
            var parsed = parser.Parse(line.Text);
            if (line.Stream != OutputStream.Stderr && !parsed.IsErrorMatch || string.IsNullOrWhiteSpace(parsed.Text))
                continue;
            var text = Mask(parsed.Text, secrets);
            var location = SourceLocationParser.Find(text)?.Select(resolver.Resolve).FirstOrDefault(l => l is not null);
            errors.Add(location is null ? new ErrorLine(Shorten(text)) : new ErrorLine(Shorten(text), location.Path, location.Line, location.Column));
            if (errors.Count == RunRecord.MaxErrors)
                break;
        }
        return errors.Count > 0 ? errors : null;
    }

    /// <summary>
    /// Records each step as it starts, nested and <c>forEach</c> ones and retries included, linked to the workflow's own
    /// record unless the workflow only runs a script's prerequisites.
    /// </summary>
    /// <returns>The step records in start order, once the workflow has finished and every step it started is recorded.</returns>
    public static async Task<IReadOnlyList<RunRecord>> AttachSteps(WorkflowRun run, HistoryStore store, Func<RunRequest, string> keyOf, string trigger = RunTriggers.Manual)
    {
        var attached = new Dictionary<RunHandle, Task<RunRecord>>();
        var parentRunId = run.Request.Target is null ? run.RunId : null;
        run.StepChanged += step =>
        {
            if (step is not { Handle: { } handle, Request: { } request })
                return;
            lock (attached)
                if (!attached.ContainsKey(handle))
                    attached[handle] = Attach(handle, store, request, keyOf(request), trigger, name: null, parentRunId, step.Id);
        };
        await run.Completion.ConfigureAwait(false);
        Task<RunRecord>[] recording;
        lock (attached)
            recording = [.. attached.Values];
        return await Task.WhenAll(recording).ConfigureAwait(false);
    }

    /// <summary>Records the steps, then the workflow itself: exit 0 when it succeeded, else 1, with one "step: status" log line per step.</summary>
    public static async Task<RunRecord> AttachWorkflow(WorkflowRun run, HistoryStore store, RunRecord template, Func<RunRequest, string> keyOf,
        string stepTrigger)
    {
        var steps = AttachSteps(run, store, keyOf, stepTrigger);
        var startedAt = store.Time.GetLocalNow();
        var result = await run.Completion.ConfigureAwait(false);
        var record = store.Add(Describe(template, run.Request.Tree, run.Workflow, run.Request.Tree.BaseDirectory) with
        {
            Id = run.RunId,
            NodeId = run.Workflow.Id,
            StartedAt = startedAt,
            Duration = result.Duration,
            Outcome = result.Outcome == WorkflowOutcome.Stopped ? RunOutcome.Stopped : RunOutcome.Exited,
            ExitCode = result.Succeeded ? 0 : 1,
        }, run.Steps.Select(s => $"{s.Id}: {s.Status}"));
        await steps.ConfigureAwait(false);
        return record;
    }

    private static string Mask(string text, IReadOnlyCollection<string>? secrets) => secrets is null ? text : SecretMasker.Mask(text, secrets);
}
