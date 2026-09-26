using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BatchPad.Core.Arguments;
using BatchPad.Core.Choices;
using BatchPad.Core.Customisation;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Workflows;

/// <summary>A workflow to run, with the tree it is defined in (bare step references resolve there).</summary>
public sealed record WorkflowRequest(ScriptTree Tree, WorkflowNode Workflow)
{
    public IReadOnlyDictionary<string, JsonNode?>? Values { get; init; }

    /// <summary>Per-step value overrides by step id, as a customisation's <c>stepValues</c> (§3.7).</summary>
    public IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? StepValues { get; init; }

    public IEnumerable<KeyValuePair<string, string>>? BaseEnvironment { get; init; }

    public static WorkflowRequest From(ResolvedCustomisation customisation) =>
        customisation is { Definition: WorkflowNode workflow, DefinitionTree: { } tree }
            ? new WorkflowRequest(tree, workflow) { Values = customisation.Values, StepValues = customisation.StepValues }
            : throw new InvalidOperationException(customisation.Problem ?? $"'{customisation.Name}' is not a workflow.");
}

/// <summary>
/// Runs workflows for the MVP (§4.1): steps in order with <c>when</c>, parameters flowing by name with their types,
/// <c>forEach</c>, per-step <c>emptyArgs</c>, nested workflows, and a long-running last step that ends the workflow at ready.
/// </summary>
public sealed partial class WorkflowRunner(LoadedWorkspace workspace, IStepLauncher launcher, IShellOpener? opener = null)
{
    private readonly ChoiceResolver _choices = new();

    public WorkflowRunner(LoadedWorkspace workspace, RunGate gate, InterpreterLocator interpreters, IShellOpener? opener = null)
        : this(workspace, new GatedStepLauncher(gate, interpreters), opener)
    {
    }

    /// <exception cref="WorkflowException">The workflow runs itself.</exception>
    public WorkflowRun Start(WorkflowRequest request)
    {
        if (WorkflowValidator.CycleFrom(request.Workflow, request.Tree.Kind, workspace.References) is { } cycle)
            throw new WorkflowException($"Workflow '{ScriptTree.DisplayName(request.Workflow)}' runs itself: {cycle}.");

        var steps = request.Workflow.Steps.Select((step, index) => new StepRun(step, step.Id ?? $"step{index + 1}")).ToList();
        var run = new WorkflowRun(request.Workflow, steps, launcher);
        _ = Task.Run(async () =>
        {
            try
            {
                run.Complete(await ExecuteAsync(run, request));
            }
            catch (Exception ex)
            {
                foreach (var step in steps.Where(s => s.Status == StepStatus.Pending))
                    (step.Error, step.Status) = (ex.Message, StepStatus.Failed);
                run.Complete(WorkflowOutcome.Failed);
            }
        });
        return run;
    }

    private async Task<WorkflowOutcome> ExecuteAsync(WorkflowRun run, WorkflowRequest request)
    {
        var parameters = BindParameters(request);
        var failed = false;
        var stepResults = new Dictionary<string, IReadOnlyDictionary<string, string>>();
        for (var index = 0; index < run.Steps.Count; index++)
        {
            var step = run.Steps[index];
            var shouldRun = (step.Step.When ?? StepWhen.Success) switch
            {
                StepWhen.Failure => failed,
                StepWhen.Always => true,
                _ => !failed,
            };
            if (run.IsStopping || !shouldRun)
            {
                run.Update(step, StepStatus.Skipped);
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
            var succeeded = await RunStepAsync(run, step, request, parameters, context, isLast: index == run.Steps.Count - 1);
            failed |= !succeeded;
            stepResults[step.Id] = new Dictionary<string, string>
            {
                ["result"] = succeeded ? "success" : "failure",
                ["exitCode"] = step.Result?.ExitCode.ToString() ?? "",
            };
        }
        return run.IsStopping ? WorkflowOutcome.Stopped : failed ? WorkflowOutcome.Failed : WorkflowOutcome.Succeeded;
    }

    private async Task<bool> RunStepAsync(WorkflowRun run, StepRun step, WorkflowRequest request, BoundParameters parameters,
        TemplateContext context, bool isLast)
    {
        if (step.Step.Run is not { } reference
            || ReferenceResolver.Parse(reference, request.Tree.Kind) is not { } parsed
            || workspace.References.Resolve(reference, request.Tree.Kind) is not RunnableNode target)
            return Fail(run, step, $"Step '{step.Id}' runs '{step.Step.Run}', which does not exist.");
        var targetTree = workspace.Trees.First(t => t.Kind == parsed.Tree);

        if (step.Step.ForEach is not { } forEach)
            return await RunTargetAsync(run, step, request, parameters, target, targetTree, context, isLast);

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
        var allSucceeded = true;
        foreach (var item in items)
        {
            if (run.IsStopping)
                break;
            var row = step.AddItem(item.Text);
            run.Raise(step);
            allSucceeded &= await RunTargetAsync(run, row, request, parameters, target, targetTree, context with { Item = item }, isLast: false);
        }
        run.Update(step, run.IsStopping ? StepStatus.Stopped : allSucceeded ? StepStatus.Succeeded : StepStatus.Failed);
        return allSucceeded && !run.IsStopping;
    }

    private async Task<bool> RunTargetAsync(WorkflowRun run, StepRun row, WorkflowRequest request, BoundParameters parameters,
        RunnableNode target, ScriptTree targetTree, TemplateContext context, bool isLast)
    {
        Dictionary<string, JsonNode?> values;
        try
        {
            values = StepValues(row, request, parameters.Flowing, target, context);
        }
        catch (Exception ex) when (ex is TemplateException or ArgumentAssemblyException)
        {
            return Fail(run, row, ex.Message);
        }

        if (target is WorkflowNode nestedWorkflow)
        {
            var nested = Start(new WorkflowRequest(targetTree, nestedWorkflow) { Values = values, BaseEnvironment = request.BaseEnvironment });
            nested.StepChanged += run.Raise;
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

        var script = WithEmptyArgs((ScriptNode)target, row.Step.EmptyArgs);
        var runRequest = new RunRequest(workspace, targetTree, script) { Values = values, BaseEnvironment = request.BaseEnvironment };
        try
        {
            row.Handle = launcher.Start(runRequest);
        }
        catch (Exception ex) when (ex is RunException or UntrustedWorkspaceException or TemplateException or ArgumentAssemblyException)
        {
            return Fail(run, row, ex.Message);
        }
        row.Request = runRequest;
        row.Artifacts = ResolveArtifacts(runRequest);
        run.Update(row, StepStatus.Running);
        if (run.IsStopping)
            await run.StopStepAsync(row);

        if (isLast && script.LongRunning == true && await WaitForReadyAsync(row, runRequest) is { } signal)
        {
            row.ReadySignal = signal;
            run.Update(row, StepStatus.Ready);
            return true;
        }

        var outcome = await row.Handle.Completion;
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
        var choiceContext = new ChoiceContext(workspace.Directory) { Lists = file.Lists, Templates = templates };
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
    private Dictionary<string, JsonNode?> StepValues(StepRun row, WorkflowRequest request, IReadOnlySet<string> flowing,
        RunnableNode target, TemplateContext context)
    {
        var values = new Dictionary<string, JsonNode?>();
        foreach (var parameter in SharedParameters.MergeAll(target.Params, workspace.Workspace.File.SharedParams))
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

    private ScriptNode WithEmptyArgs(ScriptNode script, List<string>? emptyArgs)
    {
        if (emptyArgs is null)
            return script;
        var copy = CustomisationResolver.Clone(script);
        copy.Params = [.. SharedParameters.MergeAll(copy.Params, workspace.Workspace.File.SharedParams)];
        foreach (var parameter in copy.Params.Where(p => p.Type == ParameterType.Multichoice))
            parameter.EmptyArgs = emptyArgs;
        return copy;
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
