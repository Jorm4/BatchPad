using System.Text.Json.Nodes;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.History;
using BatchPad.Core.Config;
using BatchPad.Core.Customisation;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Trust;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Cli;

/// <summary>Runs the <c>run</c> and <c>list</c> verbs headless (§9.3), unattended and recorded with the <c>cli</c> trigger.</summary>
public sealed class CliRunner
{
    public const int UsageError = 2;
    public const int Failure = 1;

    private readonly AppPaths _paths;
    private readonly TextWriter _out;
    private readonly TextWriter _error;
    private readonly TrustStore _trust;
    private readonly IRunLauncher _launcher;
    private readonly IWorkflowLauncher _workflows;

    public CliRunner(AppPaths paths, Settings settings, TextWriter output, TextWriter error,
        IRunLauncher? launcher = null, IWorkflowLauncher? workflows = null)
    {
        _paths = paths;
        _out = output;
        _error = error;
        var interpreters = new InterpreterLocator(settings.Interpreters);
        _trust = new TrustStore(settings, paths.SettingsFile);
        var gate = new RunGate(_trust);
        _launcher = launcher ?? new GatedRunLauncher(gate, interpreters);
        _workflows = workflows ?? new GatedWorkflowLauncher(gate, interpreters, new ShellOpener(new ShellService()));
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, string currentDirectory)
    {
        CliCommand command;
        try
        {
            command = CliCommand.Parse(args);
        }
        catch (CliUsageException ex)
        {
            _error.WriteLine(ex.Message);
            _error.WriteLine(CliCommand.Usage);
            return UsageError;
        }

        LoadedWorkspace workspace;
        try
        {
            if (WorkspaceLocator.Locate(command.Workspace, currentDirectory, []) is not { } file)
            {
                _error.WriteLine("No batchpad.json found here or above; pass --workspace <path>.");
                return UsageError;
            }
            workspace = WorkspaceLoader.Load(file, _paths, _trust);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ConfigException)
        {
            _error.WriteLine(ex.Message);
            return Failure;
        }
        foreach (var problem in workspace.Errors)
            _error.WriteLine($"{problem.FilePath}: {problem.Message}");

        if (command.Verb == CliVerb.List)
        {
            foreach (var entry in Entries(workspace))
                _out.WriteLine($"{entry.Reference}\t{entry.Name}");
            return 0;
        }
        return await RunAsync(command, workspace);
    }

    private async Task<int> RunAsync(CliCommand command, LoadedWorkspace workspace)
    {
        var matches = Find(workspace, command.Target!);
        if (matches.Count != 1)
        {
            _error.WriteLine(matches.Count == 0
                ? $"No script or workflow '{command.Target}'. 'batchpad list' shows the ids."
                : $"'{command.Target}' matches {matches.Count} entries; use an id: {string.Join(", ", matches.Select(m => m.Reference))}");
            return UsageError;
        }
        var target = matches[0];
        var values = new Dictionary<string, JsonNode?>(target.Values ?? new Dictionary<string, JsonNode?>());
        foreach (var (name, value) in command.Values)
            values[name] = JsonValue.Create(value);
        var store = HistoryStore.For(_paths, workspace.Id);

        try
        {
            if (target.Definition is WorkflowNode workflow)
            {
                var request = new WorkflowRequest(target.Tree, workflow)
                {
                    Values = values,
                    StepValues = target.StepValues,
                    Unattended = true,
                    Confirmed = command.Yes,
                };
                return await RunWorkflowAsync(workspace, request, store, recordAs: target);
            }

            var runRequest = new RunRequest(workspace, target.Tree, (ScriptNode)target.Definition)
            {
                Values = values,
                ExtraArguments = target.ExtraArguments,
                Unattended = true,
                Confirmed = command.Yes,
            };
            RunPlanner.CheckUnattended(runRequest);
            if (Prerequisites.WorkflowFor(runRequest) is { } withPrerequisites)
                return await RunWorkflowAsync(workspace, withPrerequisites, store, recordAs: null);

            using var process = _launcher.Start(runRequest);
            var recording = HistoryRecorder.Attach(process, store, runRequest, target.Key, RunTriggers.Cli, target.Name);
            using (process.Subscribe(Write))
            {
                var result = await process.Completion;
                await recording;
                return ExitCodeOf(result);
            }
        }
        catch (Exception ex) when (ex is WorkflowException or InvalidOperationException || RunProblems.IsRunProblem(ex))
        {
            _error.WriteLine(ex.Message);
            return Failure;
        }
    }

    private async Task<int> RunWorkflowAsync(LoadedWorkspace workspace, WorkflowRequest request, HistoryStore store, Entry? recordAs)
    {
        var run = _workflows.Start(workspace, request);
        var streamed = new HashSet<StepRun>();
        var subscriptions = new List<IDisposable>();
        run.StepChanged += step =>
        {
            if (step.Handle is not { } handle)
                return;
            lock (streamed)
            {
                if (!streamed.Add(step))
                    return;
                _error.WriteLine($"> {step.Id}{(step.Item is null ? "" : $" [{step.Item}]")}");
                subscriptions.Add(handle.Subscribe(Write));
            }
        };
        var recording = recordAs is null
            ? HistoryRecorder.AttachSteps(run, store, HistoryViewModel.KeyOf, RunTriggers.Cli)
            : HistoryRecorder.AttachWorkflow(run, store,
                new RunRecord { NodeKey = recordAs.Key, Tree = recordAs.Tree.Kind, Name = recordAs.Name, Trigger = RunTriggers.Cli },
                HistoryViewModel.KeyOf, RunTriggers.Cli);
        var result = await run.Completion;
        await recording;
        lock (streamed)
            subscriptions.ForEach(s => s.Dispose());
        foreach (var step in run.Steps.Where(s => s.Error is not null))
            _error.WriteLine($"{step.Id}: {step.Error}");
        if (result.Succeeded)
            return 0;
        return run.Steps.Select(s => s.Result).LastOrDefault(r => r is { Succeeded: false }) is { } failed ? ExitCodeOf(failed) : Failure;
    }

    private void Write(OutputLine line)
    {
        var writer = line.Stream == OutputStream.Stdout ? _out : _error;
        lock (writer)
            writer.WriteLine(line.Text);
    }

    private static int ExitCodeOf(RunResult result) =>
        result.Outcome == RunOutcome.Exited ? result.ExitCode : result.ExitCode != 0 ? result.ExitCode : Failure;

    private sealed record Entry(string Reference, string Name, ScriptTree Tree, RunnableNode Definition, string Key)
    {
        public IReadOnlyDictionary<string, JsonNode?>? Values { get; init; }
        public IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? StepValues { get; init; }
        public string? ExtraArguments { get; init; }
    }

    /// <summary>An id or reference, where the workspace's own id wins over a My Scripts one; else a unique name.</summary>
    private static List<Entry> Find(LoadedWorkspace workspace, string target)
    {
        var entries = Entries(workspace).ToList();
        return entries.FirstOrDefault(e => e.Reference == target) is { } byReference
            ? [byReference]
            : entries.Where(e => string.Equals(e.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Every runnable entry: the workspace's, then My Scripts (with their customisations applied), then global ones.</summary>
    private static IEnumerable<Entry> Entries(LoadedWorkspace workspace)
    {
        var customisations = new CustomisationResolver(workspace);
        foreach (var tree in new[] { workspace.Workspace, workspace.MyScripts, workspace.Global }.SelectMany(t => t.SelfAndParts()))
        {
            foreach (var (node, _) in tree.AllNodes())
            {
                if (node is not RunnableNode runnable || ReferenceResolver.IdOf(node) is not { Length: > 0 } id)
                    continue;
                var key = tree.NodeKey(node, (node as ScriptNode)?.Path)!;
                var reference = ReferenceResolver.Qualified(tree, id);
                if (tree.Kind == TreeKind.MyScripts && node is ScriptNode entry)
                {
                    var resolved = customisations.Resolve(entry);
                    if (resolved is { IsBroken: false, Definition: { } definition, DefinitionTree: { } definitionTree })
                        yield return new Entry(reference, resolved.Name, definitionTree, definition, key)
                        {
                            Values = resolved.Values,
                            StepValues = resolved.StepValues,
                            ExtraArguments = resolved.ExtraArgs,
                        };
                    continue;
                }
                yield return new Entry(tree.Kind == TreeKind.Workspace ? id : reference, ScriptTree.DisplayName(node), tree, runnable, key);
            }
        }
    }
}
