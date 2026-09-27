using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.App.Services;
using BatchPad.App.ViewModels.History;
using BatchPad.Core.Config;
using BatchPad.Core.Customisation;
using BatchPad.Core.History;
using BatchPad.Core.Model;
using BatchPad.Core.Output;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Trust;
using BatchPad.Core.Workflows;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Cli;

/// <summary>Runs the command-line verbs headless (§9.3, §4.5), unattended and recorded with the <c>cli</c> or an agent trigger.</summary>
public sealed class CliRunner
{
    public const int UsageError = 2;
    public const int Failure = 1;
    public const string ClaudeCodeAgent = "claude-code";

    private readonly AppPaths _paths;
    private readonly TextWriter _out;
    private readonly TextWriter _error;
    private readonly TrustStore _trust;
    private readonly IRunLauncher _launcher;
    private readonly IWorkflowLauncher _workflows;
    private readonly TelemetryPipeline _telemetry;
    private readonly bool _flushTelemetryAfterRun;

    /// <param name="telemetry">A long-lived caller's pipeline, delivering in the background; else this runner's own, flushed after each run.</param>
    public CliRunner(AppPaths paths, Settings settings, TextWriter output, TextWriter error,
        IRunLauncher? launcher = null, IWorkflowLauncher? workflows = null, TelemetryPipeline? telemetry = null)
    {
        _paths = paths;
        _out = output;
        _error = error;
        var interpreters = new InterpreterLocator(settings.Interpreters);
        _trust = new TrustStore(settings, paths.SettingsFile);
        _telemetry = telemetry ?? TelemetryPipeline.For(paths, settings);
        _flushTelemetryAfterRun = telemetry is null;
        var gate = new RunGate(_trust, locks: LockManager.For(paths));
        _launcher = launcher ?? new GatedRunLauncher(gate, interpreters);
        _workflows = workflows ?? new GatedWorkflowLauncher(gate, interpreters, new ShellOpener(new ShellService()));
    }

    public Func<string, string?> EnvironmentVariable { get; init; } = Environment.GetEnvironmentVariable;

    public IConsolePrompt Prompt { get; init; } = new ConsolePrompt();

    internal RunSummary? LastSummary { get; private set; }

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
        var trust = new CliTrust(_trust, Prompt, _out, _error, EnvironmentVariable);
        switch (command.Verb)
        {
            case CliVerb.Mcp:
                return await McpHost.RunAsync(_paths, _error);
            case CliVerb.Trust:
                return trust.Trust(command, currentDirectory);
            case CliVerb.Untrust:
                return trust.Untrust(command, currentDirectory);
        }
        return await RunAsync(command, currentDirectory);
    }

    /// <param name="trustedOnly">Refuses every verb, not only runs, in a workspace that isn't trusted.</param>
    /// <param name="cancellation">Stops the run, which is then recorded as stopped.</param>
    internal async Task<int> RunAsync(CliCommand command, string currentDirectory, bool trustedOnly = false,
        CancellationToken cancellation = default)
    {
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
        if (trustedOnly && new RunGate(_trust).Check(workspace) is { Allowed: false } refusal)
        {
            _error.WriteLine(refusal.Reason);
            return Failure;
        }
        foreach (var problem in workspace.Errors)
            _error.WriteLine($"{problem.FilePath}: {problem.Message}");

        return command.Verb switch
        {
            CliVerb.List => List(command, workspace),
            CliVerb.Log => ShowLog(command, HistoryStore.For(_paths, workspace.Id)),
            CliVerb.Stats => ShowStats(command, workspace),
            CliVerb.Compare => CliCompare.Run(command, HistoryStore.For(_paths, workspace.Id), _out, _error),
            _ => await RunAsync(command, workspace, cancellation),
        };
    }

    private int List(CliCommand command, LoadedWorkspace workspace)
    {
        var entries = Entries(workspace).Where(e => command.AllowedIds?.Contains(e.Reference) != false);
        if (command.Json)
        {
            var scripts = entries.Select(e => CliListing.Describe(e, workspace));
            _out.WriteLine(command.WithCheckout
                ? JsonSerializer.Serialize(new { checkout = Checkout.Read(workspace.Directory), scripts }, RunResultJson.Options)
                : JsonSerializer.Serialize(scripts, RunResultJson.Options));
            return 0;
        }
        foreach (var entry in entries)
            _out.WriteLine($"{entry.Reference}\t{entry.Name}");
        return 0;
    }

    private int ShowLog(CliCommand command, HistoryStore store)
    {
        if (store.Find(command.Target!) is not { } record)
        {
            _error.WriteLine($"No run '{command.Target}' in this workspace's history.");
            return UsageError;
        }
        IEnumerable<string> lines;
        try
        {
            lines = command.ErrorsOnly ? (record.Errors ?? []).Select(e => e.Text) : File.ReadAllLines(store.LogPath(record));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _error.WriteLine(ex.Message);
            return Failure;
        }
        if (command.FirstLine is { } first)
            lines = lines.Skip(first - 1);
        if (command.LastLine is { } last)
            lines = lines.Take(last - (command.FirstLine ?? 1) + 1);
        foreach (var line in command.Tail is { } tail ? lines.TakeLast(tail) : lines)
            _out.WriteLine(line);
        return 0;
    }

    private int ShowStats(CliCommand command, LoadedWorkspace workspace)
    {
        var stats = RunStats.Compute(HistoryStore.For(_paths, workspace.Id).Recent(), command.Since, DateTimeOffset.Now);
        if (command.Json && command.WithCheckout)
        {
            var json = JsonSerializer.SerializeToNode(stats, RunResultJson.Options)!.AsObject();
            json["checkout"] = JsonSerializer.SerializeToNode(Checkout.Read(workspace.Directory), RunResultJson.Options);
            _out.WriteLine(json.ToJsonString(RunResultJson.Options));
        }
        else if (command.Json)
            _out.WriteLine(JsonSerializer.Serialize(stats, RunResultJson.Options));
        else
            CliStatsFormatter.Write(stats, _out);
        return 0;
    }

    private string TriggerFor(CliCommand command) =>
        command.Agent is { Length: > 0 } agent ? RunTriggers.Agent(agent)
        : EnvironmentVariable("CLAUDECODE") == "1" ? RunTriggers.Agent(ClaudeCodeAgent)
        : RunTriggers.Cli;

    private async Task<int> RunAsync(CliCommand command, LoadedWorkspace workspace, CancellationToken cancellation)
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
        if (command.AllowedIds is { } allowed && !allowed.Contains(target.Reference))
        {
            _error.WriteLine($"'{target.Reference}' is not in mcp.allowIds in the BatchPad settings.");
            return UsageError;
        }
        var values = new Dictionary<string, JsonNode?>(target.Values ?? new Dictionary<string, JsonNode?>());
        foreach (var (name, value) in command.JsonValues)
            values[name] = value?.DeepClone();
        foreach (var (name, value) in command.Values)
            values[name] = JsonValue.Create(value);
        var store = HistoryStore.For(_paths, workspace.Id);
        var telemetry = _telemetry.Attach(store, TelemetryEvents.WorkspaceOf(workspace));
        await using var flushedTelemetry = _flushTelemetryAfterRun ? telemetry : null;
        using var sharedTelemetry = _flushTelemetryAfterRun ? null : telemetry;
        var run = new CliRun(command, TriggerFor(command), store, target);
        if (!command.Json && Checkout.Read(workspace.Directory) is { } checkout)
            _error.WriteLine($"in {checkout.Describe()}");

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
                return await RunWorkflowAsync(workspace, request, run, recordAs: target, cancellation);
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
                return await RunWorkflowAsync(workspace, withPrerequisites, run, recordAs: null, cancellation);

            using var process = _launcher.Start(runRequest, waitForLocks: !command.NoWait);
            await using var stop = cancellation.Register(() => _ = process.StopAsync());
            var reportWaiting = WaitingReporter(() => process.WaitingForLock);
            process.WaitingChanged += reportWaiting;
            reportWaiting();
            var recording = HistoryRecorder.Attach(process, store, runRequest, target.Key, run.Trigger, target.Name);
            using (Relay(process, runRequest, command))
            {
                var result = await process.Completion;
                Report(run, RunSummary.From(await recording, store, target.Reference, steps: []));
                return ExitCodeOf(result);
            }
        }
        catch (LockBusyException ex)
        {
            _error.WriteLine($"{ex.Message} Not waiting because of --no-wait.");
            return Failure;
        }
        catch (Exception ex) when (ex is WorkflowException or InvalidOperationException || RunProblems.IsRunProblem(ex))
        {
            _error.WriteLine(ex.Message);
            return Failure;
        }
    }

    private async Task<int> RunWorkflowAsync(LoadedWorkspace workspace, WorkflowRequest request, CliRun cli, Entry? recordAs,
        CancellationToken cancellation)
    {
        if (cli.Command.NoWait)
        {
            _error.WriteLine($"--no-wait works only for a single script, and '{cli.Target.Reference}' runs several steps.");
            return UsageError;
        }
        var run = _workflows.Start(workspace, request);
        var streamed = new HashSet<StepRun>();
        var subscriptions = new List<IDisposable?>();
        run.StepChanged += step =>
        {
            if (step is not { Handle: { } handle, Request: { } stepRequest })
                return;
            lock (streamed)
            {
                if (!streamed.Add(step))
                    return;
                if (!cli.Command.Json)
                    _error.WriteLine($"> {step.Id}{(step.Item is null ? "" : $" [{step.Item}]")}");
                var reportWaiting = WaitingReporter(() => handle.WaitingForLock);
                handle.WaitingChanged += reportWaiting;
                reportWaiting();
                subscriptions.Add(Relay(handle, stepRequest, cli.Command));
            }
        };
        var steps = recordAs is null ? HistoryRecorder.AttachSteps(run, cli.Store, HistoryViewModel.KeyOf, cli.Trigger) : null;
        var workflowRecord = recordAs is null
            ? null
            : HistoryRecorder.AttachWorkflow(run, cli.Store,
                new RunRecord { NodeKey = recordAs.Key, Tree = recordAs.Tree.Kind, Name = recordAs.Name, Trigger = cli.Trigger },
                HistoryViewModel.KeyOf, cli.Trigger);
        WorkflowResult result;
        await using (cancellation.Register(() => _ = run.StopAsync()))
            result = await run.Completion;
        if (workflowRecord is not null)
            Report(cli, RunSummary.From(await workflowRecord, cli.Store, cli.Target.Reference));
        else if (await steps! is { Count: > 0 } records)
        {
            var last = records[^1];
            Report(cli, RunSummary.From(last, cli.Store, last.NodeId == cli.Target.Definition.Id ? cli.Target.Reference : null, records.SkipLast(1)));
        }
        lock (streamed)
            subscriptions.ForEach(s => s?.Dispose());
        foreach (var step in run.Steps.Where(s => s.Error is not null))
            _error.WriteLine($"{step.Id}: {step.Error}");
        if (result.Succeeded)
            return 0;
        return run.Steps.Select(s => s.Result).LastOrDefault(r => r is { Succeeded: false }) is { } failed ? ExitCodeOf(failed) : Failure;
    }

    private IDisposable? Relay(IRunOutput output, RunRequest request, CliCommand command)
    {
        if (command.Json)
            return null;
        if (!command.ErrorsOnly)
            return output.Subscribe(Write);
        var parser = new OutputLineParser(request.Script.ErrorPatterns);
        return output.Subscribe(line =>
        {
            if (line.Stream == OutputStream.Stderr || line.Stream == OutputStream.Stdout && parser.Parse(line.Text).IsErrorMatch)
                Write(line);
        });
    }

    private void Report(CliRun run, RunSummary summary)
    {
        LastSummary = summary;
        if (run.Command.Json)
        {
            lock (_out)
                _out.WriteLine(RunResultJson.Serialize(summary));
        }
        else if (run.Command.ErrorsOnly)
        {
            lock (_out)
                WriteSummary(summary);
        }
    }

    private void WriteSummary(RunSummary summary)
    {
        var queued = summary.QueuedMs >= 100 ? $", queued {Seconds(summary.QueuedMs)}" : "";
        _out.WriteLine($"{summary.Name}: {OutcomeText(summary)} after {Seconds(summary.DurationMs)}{queued}");
        foreach (var step in summary.Steps.Where(s => !s.Succeeded))
            _out.WriteLine($"  {step.Id ?? step.Name}: {OutcomeText(step)}");
        if (summary.Tests is { } tests)
        {
            _out.WriteLine($"tests: {tests.Passed} passed, {tests.Failed} failed, {tests.Skipped} skipped");
            foreach (var name in tests.FailedNames)
                _out.WriteLine($"  failed: {name}");
        }
        _out.WriteLine($"log: {summary.LogPath}");
    }

    private static string OutcomeText(RunSummary summary) => summary switch
    {
        { Succeeded: true } => "succeeded",
        { Outcome: RunOutcome.Exited } => $"failed with exit code {summary.ExitCode}",
        { Outcome: RunOutcome.TimedOut } => "timed out",
        { Outcome: RunOutcome.FailedToStart } => "failed to start",
        _ => "stopped",
    };

    private static string Seconds(long milliseconds) =>
        (milliseconds / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s";

    private Action WaitingReporter(Func<string?> waitingFor)
    {
        string? reported = null;
        return () =>
        {
            lock (_error)
            {
                if (waitingFor() is not { } lockName || lockName == reported)
                    return;
                reported = lockName;
                _error.WriteLine($"waiting for lock {lockName}");
            }
        };
    }

    private void Write(OutputLine line)
    {
        var writer = line.Stream == OutputStream.Stdout ? _out : _error;
        lock (writer)
            writer.WriteLine(line.Text);
    }

    private static int ExitCodeOf(RunResult result) =>
        result.Outcome == RunOutcome.Exited ? result.ExitCode : result.ExitCode != 0 ? result.ExitCode : Failure;

    private sealed record CliRun(CliCommand Command, string Trigger, HistoryStore Store, Entry Target);

    internal sealed record Entry(string Reference, string Name, ScriptTree Tree, RunnableNode Definition, string Key)
    {
        public IReadOnlyDictionary<string, JsonNode?>? Values { get; init; }
        public IReadOnlyDictionary<string, Dictionary<string, JsonNode?>>? StepValues { get; init; }
        public string? ExtraArguments { get; init; }
        public string? Folder { get; init; }
        public string? Description { get; init; }
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
                            Folder = tree.FolderOf(entry),
                            Description = entry.Description ?? definition.Description,
                        };
                    continue;
                }
                yield return new Entry(tree.Kind == TreeKind.Workspace ? id : reference, ScriptTree.DisplayName(node), tree, runnable, key)
                {
                    Folder = tree.FolderOf(runnable),
                    Description = runnable.Description,
                };
            }
        }
    }
}
