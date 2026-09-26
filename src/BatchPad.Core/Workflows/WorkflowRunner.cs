using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BatchPad.Core.Arguments;
using BatchPad.Core.Choices;
using BatchPad.Core.Config;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Workflows;

/// <summary>Runs workflows (§4.1).</summary>
public sealed partial class WorkflowRunner(
    LoadedWorkspace workspace, IStepLauncher launcher, IShellOpener? opener = null, LockManager? locks = null, TimeProvider? time = null)
{
    private readonly ChoiceResolver _choices = new();
    private readonly LockManager _locks = locks ?? new LockManager();
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public WorkflowRunner(LoadedWorkspace workspace, RunGate gate, InterpreterLocator interpreters, IShellOpener? opener = null)
        : this(workspace, new GatedStepLauncher(gate, interpreters), opener, gate.Locks, gate.Time)
    {
    }

    /// <exception cref="WorkflowException">The workflow runs itself.</exception>
    public WorkflowRun Start(WorkflowRequest request) => Start(request, lockOwner: null, stepChanged: null);

    private WorkflowRun Start(WorkflowRequest request, object? lockOwner, Action<StepRun>? stepChanged)
    {
        if (WorkflowValidator.CycleFrom(request.Workflow, request.Tree, workspace.References) is { } cycle)
            throw new WorkflowException($"Workflow '{ScriptTree.DisplayName(request.Workflow)}' runs itself: {cycle}.");
        if (request is { Unattended: true, Confirmed: false }
            && (request.Workflow.Confirm is { Length: > 0 } || request.Workflow.Steps.SelectMany(s => s.Leaves()).Any(s => s.Confirm == true)))
            throw new WorkflowException($"'{ScriptTree.DisplayName(request.Workflow)}' needs confirmation, and nobody is there to give it.");

        var steps = request.Workflow.Steps.Select((step, index) => new StepRun(step, step.Id ?? $"step{index + 1}")).ToList();
        var resumeAt = 0;
        if (request.Resume is { } resume
            && (resumeAt = steps.FindIndex(s => s.SelfAndChildren().Any(c => c.Id == resume.StepId))) < 0)
            throw new WorkflowException($"'{ScriptTree.DisplayName(request.Workflow)}' has no step '{resume.StepId}' to re-run from.");
        var run = new WorkflowRun(request, steps, launcher, lockOwner);
        run.StepChanged += stepChanged;
        _ = Task.Run(async () =>
        {
            try
            {
                var lockNames = LocksHeldThroughout(request);
                using var lease = lockNames.Count == 0 ? null : await _locks.AcquireAsync(lockNames, run.LockOwner, run.Wait, run.StopRequested,
                    holder: ScriptTree.DisplayName(request.Workflow));
                run.Wait(null);
                run.Complete(await ExecuteAsync(run, request, resumeAt));
            }
            catch (OperationCanceledException) when (run.IsStopping)
            {
                foreach (var step in steps.SelectMany(s => s.SelfAndChildren()))
                    step.Status = StepStatus.Skipped;
                run.Complete(WorkflowOutcome.Stopped);
            }
            catch (Exception ex)
            {
                foreach (var step in steps.SelectMany(s => s.SelfAndChildren()).Where(s => s.Status == StepStatus.Pending))
                    (step.Error, step.Status) = (ex.Message, StepStatus.Failed);
                run.Complete(WorkflowOutcome.Failed);
            }
        });
        return run;
    }

    /// <summary>
    /// A workflow's <c>lock</c> plus every lock its steps take, so all are taken in one ordered acquire: taking a step's
    /// lock later, while holding the workflow's, could deadlock with a run that holds them the other way round.
    /// </summary>
    private List<string> LocksHeldThroughout(WorkflowRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Workflow.Lock))
            return [];
        var names = new List<string>();
        var visited = new HashSet<TreeNode>(ReferenceEqualityComparer.Instance);
        Add(request.Workflow, request.Tree);
        if (request.Target?.Script.Lock is { } targetLock && !string.IsNullOrWhiteSpace(targetLock))
            names.Add(targetLock);
        return names;

        void Add(RunnableNode node, ScriptTree tree)
        {
            if (!visited.Add(node))
                return;
            if (!string.IsNullOrWhiteSpace(node.Lock))
                names.Add(node.Lock);
            if (node is not WorkflowNode workflow)
                return;
            foreach (var step in workflow.Steps.SelectMany(s => s.Leaves()))
            {
                if (step.Run is { } reference && workspace.References.Resolve(reference, tree) is RunnableNode target
                    && workspace.References.TreeOf(target) is { } targetTree)
                    Add(target, targetTree);
            }
        }
    }

    private async Task<WorkflowOutcome> ExecuteAsync(WorkflowRun run, WorkflowRequest request, int resumeAt)
    {
        var parameters = BindParameters(request);
        var failed = false;
        var stepResults = run.StepResults;
        foreach (var (id, results) in request.Resume?.StepResults ?? new Dictionary<string, IReadOnlyDictionary<string, string>>())
            stepResults[id] = results;
        for (var index = 0; index < run.Steps.Count; index++)
        {
            var step = run.Steps[index];
            var isLast = index == run.Steps.Count - 1;
            if (index < resumeAt)
            {
                foreach (var row in step.SelfAndChildren())
                    run.Update(row, StepStatus.Reused);
                continue;
            }
            if (!ShouldRun(run, step, failed))
            {
                Skip(run, step);
                continue;
            }

            var context = parameters.Templates with
            {
                Workflow = new Dictionary<string, string>
                {
                    ["name"] = ScriptTree.DisplayName(request.Workflow),
                    ["result"] = failed ? "failure" : "success",
                },
                Steps = stepResults,
            };
            bool succeeded;
            if (step.IsGroup)
                succeeded = await RunGroupAsync(run, step, request, parameters, context, failed, isLast, stepResults);
            else if (isLast && request.Target is { } dependent)
                succeeded = await LaunchAsync(run, step, dependent with { LockOwner = run.LockOwner }, isLast);
            else
                succeeded = await RunStepAsync(run, step, request, parameters, context, isLast);
            Record(stepResults, step, succeeded);
            if (!Counts(step, succeeded) && !failed)
                (failed, run.FailedStep) = (true, step);
        }
        return run.IsStopping ? WorkflowOutcome.Stopped : failed ? WorkflowOutcome.Failed : WorkflowOutcome.Succeeded;
    }

    private static bool ShouldRun(WorkflowRun run, StepRun step, bool failed) =>
        !run.IsStopping && (step.Step.When ?? StepWhen.Success) switch
        {
            StepWhen.Failure => failed,
            StepWhen.Always => true,
            _ => !failed,
        };

    private static bool Counts(StepRun step, bool succeeded) => succeeded || step.Step.ContinueOnError == true;

    private static void Skip(WorkflowRun run, StepRun step)
    {
        foreach (var row in step.SelfAndChildren())
            run.Update(row, StepStatus.Skipped);
    }

    private static void Record(Dictionary<string, IReadOnlyDictionary<string, string>> stepResults, StepRun step, bool succeeded) =>
        stepResults[step.Id] = new Dictionary<string, string>(step.Outputs)
        {
            ["result"] = succeeded ? "success" : "failure",
            ["exitCode"] = step.Result?.ExitCode.ToString() ?? "",
        };

    /// <summary>Runs a group's members at once; each member's <c>when</c> sees the state from before the group.</summary>
    private async Task<bool> RunGroupAsync(WorkflowRun run, StepRun group, WorkflowRequest request, BoundParameters parameters,
        TemplateContext context, bool failed, bool isLast, Dictionary<string, IReadOnlyDictionary<string, string>> stepResults)
    {
        run.Update(group, StepStatus.Running);
        lock (stepResults)
            context = context with { Steps = new Dictionary<string, IReadOnlyDictionary<string, string>>(stepResults) };
        var results = await Task.WhenAll(group.Members.Select(async member =>
        {
            if (!ShouldRun(run, member, failed))
            {
                Skip(run, member);
                return (Member: member, Succeeded: true, Skipped: true);
            }
            var succeeded = member.IsGroup
                ? await RunGroupAsync(run, member, request, parameters, context, failed, isLast, stepResults)
                : await RunStepAsync(run, member, request, parameters, context, isLast);
            return (Member: member, Succeeded: succeeded, Skipped: false);
        }));
        lock (stepResults)
            foreach (var (member, succeeded, _) in results.Where(r => !r.Skipped))
                Record(stepResults, member, succeeded);
        run.Update(group, run.IsStopping ? StepStatus.Stopped
            : group.Members.Any(m => m.Status == StepStatus.Failed) ? StepStatus.Failed
            : StepStatus.Succeeded);
        return results.All(r => Counts(r.Member, r.Succeeded));
    }

    private async Task<bool> RunStepAsync(WorkflowRun run, StepRun step, WorkflowRequest request, BoundParameters parameters,
        TemplateContext context, bool isLast)
    {
        if (step.Step.Run is not { } reference
            || workspace.References.Resolve(reference, request.Tree) is not RunnableNode target)
            return Fail(run, step, $"Step '{step.Id}' runs '{step.Step.Run}', which does not exist.");
        StepTarget prepared;
        try
        {
            prepared = Prepare(target, step.Step.EmptyArgs);
        }
        catch (ArgumentAssemblyException ex)
        {
            return Fail(run, step, ex.Message);
        }

        if (step.Step.ForEach is not { } forEach)
            return await RunTargetAsync(run, step, request, parameters, prepared, context, isLast);

        IReadOnlyList<TemplateValue> items;
        try
        {
            items = ForEachItems(forEach, parameters, context);
        }
        catch (Exception ex) when (ex is TemplateException or ArgumentAssemblyException)
        {
            return Fail(run, step, ex.Message);
        }

        run.Update(step, StepStatus.Running);
        var rows = step.SetItems(items.Select(item => item.Text)).Zip(items, (row, item) => (Row: row, Item: item)).ToList();
        foreach (var (row, _) in rows)
            run.Raise(row);
        var next = 0;
        var anyFailed = false;
        var failFast = step.Step.FailFast == true;

        async Task WorkAsync()
        {
            while (true)
            {
                int index;
                lock (rows)
                {
                    if (run.IsStopping || (failFast && anyFailed) || next >= rows.Count)
                        return;
                    index = next++;
                }
                var (row, item) = rows[index];
                if (!await RunTargetAsync(run, row, request, parameters, prepared, context with { Item = item }, isLast: false))
                    lock (rows)
                        anyFailed = true;
            }
        }

        await Task.WhenAll(Enumerable.Range(0, Math.Max(1, step.Step.MaxParallel ?? 1)).Select(_ => WorkAsync()));
        foreach (var (row, _) in rows.Where(r => r.Row.Status == StepStatus.Pending))
            run.Update(row, StepStatus.Skipped);
        run.Update(step, run.IsStopping ? StepStatus.Stopped : anyFailed ? StepStatus.Failed : StepStatus.Succeeded);
        return !anyFailed && !run.IsStopping;
    }

    private async Task<bool> RunTargetAsync(WorkflowRun run, StepRun row, WorkflowRequest request, BoundParameters parameters,
        StepTarget target, TemplateContext context, bool isLast)
    {
        Dictionary<string, JsonNode?> values;
        try
        {
            values = StepValues(row, request, parameters.Flowing, target.Params, context);
        }
        catch (Exception ex) when (ex is TemplateException or ArgumentAssemblyException)
        {
            return Fail(run, row, ex.Message);
        }

        if (target.Node is WorkflowNode nestedWorkflow)
        {
            var nested = Start(new WorkflowRequest(target.Tree, nestedWorkflow)
            {
                Values = values,
                BaseEnvironment = request.BaseEnvironment,
                Unattended = request.Unattended,
                Confirmed = request.Confirmed,
            }, run.LockOwner, run.Raise);
            row.Nested = nested;
            run.Update(row, StepStatus.Running);
            if (run.IsStopping)
                await nested.StopAsync();
            var result = await nested.Completion;
            run.Update(row, result.Outcome switch
            {
                WorkflowOutcome.Succeeded => StepStatus.Succeeded,
                WorkflowOutcome.Stopped => StepStatus.Stopped,
                _ => StepStatus.Failed,
            });
            return result.Succeeded;
        }

        var runRequest = new RunRequest(workspace, target.Tree, (ScriptNode)target.Node)
        {
            Values = values,
            BaseEnvironment = request.BaseEnvironment,
            LockOwner = run.LockOwner,
            Unattended = request.Unattended,
            Confirmed = request.Confirmed,
        };
        for (var attempt = 1; ; attempt++)
        {
            var succeeded = await LaunchAsync(run, row, runRequest, isLast);
            if (succeeded || attempt >= row.MaxAttempts || run.IsStopping || row.Error is not null)
                return succeeded;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, row.Step.Retry!.DelaySeconds)), _time, run.StopRequested);
            }
            catch (OperationCanceledException)
            {
                run.Update(row, StepStatus.Stopped);
                return false;
            }
        }
    }

    private async Task<bool> LaunchAsync(WorkflowRun run, StepRun row, RunRequest runRequest, bool isLast)
    {
        var script = runRequest.Script;
        try
        {
            row.Handle = launcher.Start(runRequest);
        }
        catch (Exception ex) when (ex is RunException or UntrustedWorkspaceException or TemplateException or ArgumentAssemblyException)
        {
            return Fail(run, row, ex.Message);
        }
        row.Request = runRequest;
        row.Result = null;
        row.Artifacts = ResolveArtifacts(runRequest);
        run.Update(row, StepStatus.Running);
        if (run.IsStopping)
            await run.StopStepAsync(row);

        if (isLast && script.LongRunning == true && await WaitForReadyAsync(row, runRequest) is { } signal)
        {
            row.ReadySignal = signal;
            row.Outputs = StepOutputs.From(row.Handle.Output);
            row.Handle.ReleaseLocks();
            run.Update(row, StepStatus.Ready);
            return true;
        }

        var outcome = await row.Handle.Completion;
        row.Outputs = StepOutputs.From(row.Handle.Output);
        row.Attempts = [.. row.Attempts, new StepAttempt(row.Handle, outcome)];
        row.Result = outcome;
        if (opener is not null)
            ArtifactResolver.OpenAfter(outcome, row.Artifacts, opener);
        run.Update(row, outcome.Succeeded ? StepStatus.Succeeded : outcome.Outcome == RunOutcome.Stopped ? StepStatus.Stopped : StepStatus.Failed);
        return outcome.Succeeded;
    }

    private static IReadOnlyList<ResolvedArtifact> ResolveArtifacts(RunRequest request)
    {
        try
        {
            return ArtifactResolver.Resolve(request);
        }
        catch (Exception ex) when (ex is TemplateException or ArgumentAssemblyException)
        {
            return [];
        }
    }

    private async Task<ReadySignal?> WaitForReadyAsync(StepRun row, RunRequest request)
    {
        try
        {
            using var watcher = ReadyWatcher.Watch(row.Handle!, request, opener);
            return await watcher.Ready;
        }
        catch (TemplateException)
        {
            return new ReadySignal(null, null);
        }
    }

    private static bool Fail(WorkflowRun run, StepRun step, string error)
    {
        step.Error = error;
        run.Update(step, StepStatus.Failed);
        return false;
    }

    private sealed record BoundParameters(
        TemplateContext Templates, IReadOnlyList<ParameterDefinition> Definitions, IReadOnlySet<string> Flowing, ChoiceContext Choices);

    private BoundParameters BindParameters(WorkflowRequest request)
    {
        var file = workspace.Workspace.File;
        var templates = new TemplateContext { WorkspaceDir = workspace.Directory, Variables = file.Variables };
        var choiceContext = new ChoiceContext(workspace.Directory) { Lists = file.Lists, Templates = templates, Paths = request.Tree.Paths };
        var assembly = new AssemblyRequest(new ScriptNode { Params = request.Workflow.Params }, templates)
        {
            SharedParams = file.SharedParams,
            Values = request.Values,
            Choices = p => _choices.Resolve(p, choiceContext).Choices,
        };
        var bound = ArgumentAssembler.Bind(assembly);
        var flowing = bound
            .Where(p => request.Values?.ContainsKey(p.Definition.Name!) == true || p.Definition.Default is not null)
            .Select(p => p.Definition.Name!)
            .ToHashSet();
        return new BoundParameters(
            templates with { Params = bound.ToDictionary(p => p.Definition.Name!, p => p.Value) },
            [.. bound.Select(p => p.Definition)], flowing, choiceContext);
    }

    /// <summary>Workflow parameters sharing a name with the target's, then the step's own values, then the request's per-step overrides.</summary>
    private static Dictionary<string, JsonNode?> StepValues(StepRun row, WorkflowRequest request, IReadOnlySet<string> flowing,
        IReadOnlyList<ParameterDefinition> targetParams, TemplateContext context)
    {
        var values = new Dictionary<string, JsonNode?>();
        foreach (var parameter in targetParams)
        {
            if (parameter.Name is { } name && flowing.Contains(name) && context.Params.TryGetValue(name, out var value))
                values[name] = ToJson(value);
        }
        var overrides = request.StepValues?.GetValueOrDefault(row.Id);
        foreach (var (name, value) in (row.Step.Values ?? []).Concat(overrides ?? []))
            values[name] = Expand(value, context);
        return values;
    }

    private IReadOnlyList<TemplateValue> ForEachItems(string forEach, BoundParameters parameters, TemplateContext context)
    {
        var list = TemplateExpander.Expand(forEach, context).AsList();
        var definition = ParamReference().Match(forEach) is { Success: true } match
            ? parameters.Definitions.FirstOrDefault(p => p.Name == match.Groups[1].Value)
            : null;
        var choices = definition is null ? [] : _choices.Resolve(definition, parameters.Choices).Choices;
        if (list.Count == 0 && definition?.EmptyMeans == "all")
            return [.. choices.Select(TemplateValue.OfChoice)];
        return [.. list.Select(item => choices.FirstOrDefault(c => c.Value == item) is { } choice ? TemplateValue.OfChoice(choice) : TemplateValue.Of(item))];
    }

    private sealed record StepTarget(RunnableNode Node, ScriptTree Tree, IReadOnlyList<ParameterDefinition> Params);

    private StepTarget Prepare(RunnableNode target, List<string>? emptyArgs)
    {
        var tree = workspace.References.TreeOf(target)!;
        var shared = workspace.Workspace.File.SharedParams;
        if (emptyArgs is null || target is not ScriptNode script)
            return new StepTarget(target, tree, SharedParameters.MergeAll(target.Params, shared));
        var copy = ConfigJson.Clone(script);
        copy.Params = [.. SharedParameters.MergeAll(copy.Params, shared)];
        foreach (var parameter in copy.Params.Where(p => p.Type == ParameterType.Multichoice))
            parameter.EmptyArgs = emptyArgs;
        return new StepTarget(copy, tree, copy.Params);
    }

    private static JsonNode? Expand(JsonNode? value, TemplateContext context) => value switch
    {
        JsonValue text when text.GetValueKind() == JsonValueKind.String && TemplateExpander.HasVariables(text.GetValue<string>()) =>
            ToJson(TemplateExpander.Expand(text.GetValue<string>(), context)),
        JsonArray array => new JsonArray([.. array.SelectMany(item => Expand(item, context) switch
        {
            JsonArray inner => inner.Select(i => i?.DeepClone()),
            var single => [single],
        })]),
        _ => value?.DeepClone(),
    };

    private static JsonNode ToJson(TemplateValue value) =>
        value.IsList ? new JsonArray([.. value.Items!.Select(i => (JsonNode?)JsonValue.Create(i))]) : JsonValue.Create(value.Text);

    [GeneratedRegex(@"^\$\{param:([^}.|]+)\}$")]
    private static partial Regex ParamReference();
}
