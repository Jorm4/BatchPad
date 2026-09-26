using System.Text.Json.Nodes;
using BatchPad.Core.Arguments;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.History;

/// <summary>The one hook that saves a run to history when it finishes, whoever started it.</summary>
public static class HistoryRecorder
{
    /// <summary>Records <paramref name="run"/> as <paramref name="request"/>'s script, with its secret values masked.</summary>
    public static Task<RunRecord> Attach(IRunOutput run, HistoryStore store, RunRequest request, string nodeKey,
        string trigger = RunTriggers.Manual, string? name = null)
    {
        var script = request.Script;
        var secretNames = SecretMasker.SecretNames(request);
        var values = (request.Values ?? new Dictionary<string, JsonNode?>()).ToDictionary(
            v => v.Key, v => secretNames.Contains(v.Key) ? RunRecord.Masked : v.Value?.DeepClone());
        var template = new RunRecord
        {
            NodeKey = nodeKey,
            Tree = request.Tree.Kind,
            NodeId = script.Id,
            Path = script.Path,
            Name = name ?? ScriptTree.DisplayName(script),
            Values = values,
            ExtraArguments = request.ExtraArguments,
            Trigger = trigger,
        };
        return Attach(run, store, template, SecretMasker.SecretValues(request));
    }

    /// <summary>Records <paramref name="run"/> from <paramref name="template"/>; its start, command and result are filled in.</summary>
    public static async Task<RunRecord> Attach(IRunOutput run, HistoryStore store, RunRecord template,
        IReadOnlyCollection<string>? secrets = null)
    {
        var handle = run as RunHandle;
        var attachedAt = store.Time.GetLocalNow();
        var command = handle is null ? template.Command : string.Join(Environment.NewLine, handle.Specs.Select(s => s.Command.Display));
        var lines = new List<string>();
        RunResult result;
        using (run.Subscribe(line =>
               {
                   lock (lines)
                       lines.Add(line.Text);
               }))
            result = await run.Completion.ConfigureAwait(false);
        var startedAt = handle is { StartedAt: var started } && started != default ? started : attachedAt;

        string[] log;
        lock (lines)
            log = [.. lines.Select(l => Mask(l, secrets))];
        return store.Add(template with
        {
            Command = Mask(command, secrets),
            StartedAt = startedAt,
            Duration = result.Duration,
            Outcome = result.Outcome,
            ExitCode = result.ExitCode,
        }, log);
    }

    /// <summary>Records each step as it starts, nested and <c>forEach</c> ones and retries included.</summary>
    /// <returns>Completes once the workflow has finished and every step it started is recorded.</returns>
    public static async Task AttachSteps(WorkflowRun run, HistoryStore store, Func<RunRequest, string> keyOf, string trigger = RunTriggers.Manual)
    {
        var attached = new Dictionary<RunHandle, Task>();
        run.StepChanged += step =>
        {
            if (step is not { Handle: { } handle, Request: { } request })
                return;
            lock (attached)
                if (!attached.ContainsKey(handle))
                    attached[handle] = Attach(handle, store, request, keyOf(request), trigger);
        };
        await run.Completion.ConfigureAwait(false);
        Task[] recording;
        lock (attached)
            recording = [.. attached.Values];
        await Task.WhenAll(recording).ConfigureAwait(false);
    }

    /// <summary>Records the steps, then the workflow itself: exit 0 when it succeeded, else 1, with one "step: status" log line per step.</summary>
    public static async Task<RunRecord> AttachWorkflow(WorkflowRun run, HistoryStore store, RunRecord template, Func<RunRequest, string> keyOf,
        string stepTrigger)
    {
        var steps = AttachSteps(run, store, keyOf, stepTrigger);
        var startedAt = store.Time.GetLocalNow();
        var result = await run.Completion.ConfigureAwait(false);
        var record = store.Add(template with
        {
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
