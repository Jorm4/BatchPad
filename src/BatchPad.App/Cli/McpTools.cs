using System.ComponentModel;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BatchPad.Core.Running;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BatchPad.App.Cli;

/// <summary>
/// The MCP tools, each one a <see cref="CliRunner"/> verb run in-process with its output captured. There is no default
/// workspace: one server serves agents in several worktrees, so each call names its own directory (§4.5).
/// </summary>
public sealed partial class McpTools : IAsyncDisposable
{
    public const string UnnamedAgent = "mcp";
    public const int MaxLogLines = 2000;
    private const string DirectoryHelp = "Your working folder, as an absolute path; the workspace is found from it as the command line does.";

    private readonly AppPaths _paths;
    private readonly TelemetryPipeline _telemetry;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HashSet<Task> _calls = [];
    private Settings _settings = new();

    public McpTools(AppPaths paths)
    {
        _paths = paths;
        _telemetry = new TelemetryPipeline(Path.Combine(paths.LocalDirectory, "telemetry"), () => _settings.Telemetry);
    }

    public void StartTelemetry() => _telemetry.Start();

    [McpServerTool(Name = "list_scripts", ReadOnly = true)]
    [Description("Lists the scripts and workflows the workspace can run: id, name, folder, description and parameters (type, choices, default, "
        + "required), with the git checkout they belong to.")]
    public Task<CallToolResult> ListScripts([Description(DirectoryHelp)] string directory, CancellationToken cancellation = default) =>
        InvokeAsync(directory, settings => new CliCommand(CliVerb.List) { Json = true, WithCheckout = true, AllowedIds = settings.Mcp?.AllowIds },
            cancellation);

    [McpServerTool(Name = "run_script")]
    [Description("Runs a script or workflow by id, waits for it, and returns one result object: outcome, exitCode, durationMs, queuedMs, "
        + "logPath, runId, the test summary, the error lines with their file and line, the steps, and the git checkout it ran in. "
        + "Read more of the log with get_log.")]
    public async Task<CallToolResult> RunScript(McpServer server,
        [Description(DirectoryHelp)] string directory,
        [Description("The id from list_scripts.")] string id,
        [Description("Parameter values by parameter name; a multichoice takes an array. Unset parameters use their defaults.")] JsonObject? values = null,
        [Description("True returns only the result object; false also returns the run's log, up to its last 2000 lines.")] bool errorsOnly = true,
        [Description("Confirms an entry that asks for confirmation before it runs.")] bool confirm = false,
        [Description("Returns an error at once, instead of waiting, when a lock the script needs is held.")] bool noWait = false,
        CancellationToken cancellation = default)
    {
        var agent = server.ClientInfo?.Name is { Length: > 0 } name ? name : UnnamedAgent;
        RunSummary? summary = null;
        var result = await InvokeAsync(directory, settings => new CliCommand(CliVerb.Run)
        {
            Target = id,
            JsonValues = values?.ToDictionary(p => p.Key, p => p.Value) ?? [],
            Json = true,
            Yes = confirm,
            NoWait = noWait,
            Agent = agent,
            AllowedIds = settings.Mcp?.AllowIds,
        }, cancellation, runner => summary = runner.LastSummary);
        if (!errorsOnly && result.IsError != true && summary is not null && LogOf(summary.LogPath) is { } log)
            result.Content.Add(Text(log));
        return result;
    }

    [McpServerTool(Name = "get_log", ReadOnly = true)]
    [Description("Reads a recorded run's log by its runId: all of it, the last lines, only the error lines, or a range of lines.")]
    public Task<CallToolResult> GetLog(
        [Description(DirectoryHelp)] string directory,
        [Description("The runId from run_script.")] string runId,
        [Description("Returns only the last this many lines.")] int? tail = null,
        [Description("Returns only the error lines.")] bool errorsOnly = false,
        [Description("The first line to return, counting from 1.")] int? fromLine = null,
        [Description("The last line to return.")] int? toLine = null,
        CancellationToken cancellation = default)
    {
        if (tail <= 0 || fromLine <= 0 || toLine < (fromLine ?? 1))
            throw new McpException("tail and fromLine must be positive, and toLine at least fromLine.");
        return InvokeAsync(directory,
            _ => new CliCommand(CliVerb.Log) { Target = runId, Tail = tail, ErrorsOnly = errorsOnly, FirstLine = fromLine, LastLine = toLine },
            cancellation);
    }

    [McpServerTool(Name = "get_stats", ReadOnly = true)]
    [Description("The run statistics for a period: time per script, folder, trigger and git checkout, repeated runs, the slowest and the "
        + "flaky tests.")]
    public Task<CallToolResult> GetStats(
        [Description(DirectoryHelp)] string directory,
        [Description("The period: 1d, 7d, 30d, 12h and so on.")] string since = "7d",
        CancellationToken cancellation = default)
    {
        var period = CliCommand.ParsePeriod(since, "since", message => new McpException(message));
        return InvokeAsync(directory, _ => new CliCommand(CliVerb.Stats) { Since = period, Json = true, WithCheckout = true }, cancellation);
    }

    /// <summary>Stops the runs of the calls still in flight and waits until they are recorded.</summary>
    public async ValueTask DisposeAsync()
    {
        Task[] calls;
        lock (_calls)
        {
            _shutdown.Cancel();
            calls = [.. _calls];
        }
        try
        {
            await Task.WhenAll(calls);
        }
        catch (Exception)
        {
            // Each call's failure is its own caller's; shutting down goes on regardless.
        }
        await _telemetry.FlushAsync(TelemetryPipeline.ExitFlushLimit);
        await _telemetry.DisposeAsync();
        _shutdown.Dispose();
    }

    private Task<CallToolResult> InvokeAsync(string directory, Func<Settings, CliCommand> commandFor, CancellationToken cancellation,
        Action<CliRunner>? afterRun = null)
    {
        // Checked before any file access: a UNC or device path would be opened, with the user's credentials, before the trust check.
        if (string.IsNullOrWhiteSpace(directory) || !LocalPath().IsMatch(directory))
            throw new McpException($"directory must be the absolute path of your working folder on a local drive, such as C:\\repo, not '{directory}'.");
        if (!Directory.Exists(directory))
            throw new McpException($"The directory '{directory}' does not exist.");
        lock (_calls)
        {
            if (_shutdown.IsCancellationRequested)
                throw new McpException("The BatchPad MCP server is shutting down.");
            _calls.RemoveWhere(c => c.IsCompleted);
            var call = Task.Run(() => RunAsync(directory, commandFor, afterRun, cancellation), CancellationToken.None);
            _calls.Add(call);
            return call;
        }
    }

    private async Task<CallToolResult> RunAsync(string directory, Func<Settings, CliCommand> commandFor, Action<CliRunner>? afterRun,
        CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _shutdown.Token);
        var output = new StringWriter();
        var error = new StringWriter();
        var settings = _settings = App.LoadSettings(_paths, error);
        if (settings.Mcp?.Enabled != true)
            return Error(McpSettings.TurnedOff);
        var runner = new CliRunner(_paths, settings, output, error, telemetry: _telemetry);
        var exitCode = await runner.RunAsync(commandFor(settings), directory, trustedOnly: true, stop.Token);
        afterRun?.Invoke(runner);
        return ResultOf(exitCode, output, error);
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]")]
    private static partial Regex LocalPath();

    private static CallToolResult ResultOf(int exitCode, StringWriter output, StringWriter error)
    {
        var text = output.ToString().TrimEnd();
        var problems = error.ToString().TrimEnd();
        if (exitCode != 0 && text.Length == 0)
            return Error(problems.Length > 0 ? problems : $"Failed with exit code {exitCode}.");
        var result = new CallToolResult { Content = [Text(text)] };
        if (problems.Length > 0)
            result.Content.Add(Text(problems));
        return result;
    }

    private static CallToolResult Error(string message) => new() { IsError = true, Content = [Text(message)] };

    private static TextContentBlock Text(string text) => new() { Text = text };

    private static string? LogOf(string path)
    {
        try
        {
            var kept = new Queue<string>();
            var leftOut = 0;
            foreach (var line in File.ReadLines(path))
            {
                kept.Enqueue(line);
                if (kept.Count > MaxLogLines)
                {
                    kept.Dequeue();
                    leftOut++;
                }
            }
            var text = string.Join(Environment.NewLine, kept);
            return leftOut == 0 ? text : $"[{leftOut} earlier lines left out; get_log reads them]{Environment.NewLine}{text}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
