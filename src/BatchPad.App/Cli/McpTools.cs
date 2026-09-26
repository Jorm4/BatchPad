using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using BatchPad.Core.History;
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
public sealed class McpTools(AppPaths paths)
{
    public const string UnnamedAgent = "mcp";
    private const string DirectoryHelp = "Your working folder, as an absolute path; the workspace is found from it as the command line does.";

    [McpServerTool(Name = "list_scripts", ReadOnly = true)]
    [Description("Lists the scripts and workflows the workspace can run: id, name, folder, description and parameters (type, choices, default, "
        + "required), with the git checkout they belong to.")]
    public Task<CallToolResult> ListScripts([Description(DirectoryHelp)] string directory) =>
        InvokeAsync(directory, _ => new CliCommand(CliVerb.List) { Json = true },
            (output, checkout) => new JsonObject { ["checkout"] = checkout, ["scripts"] = output });

    [McpServerTool(Name = "run_script")]
    [Description("Runs a script or workflow by id, waits for it, and returns one result object: outcome, exitCode, durationMs, queuedMs, "
        + "logPath, runId, the test summary, the error lines with their file and line, the steps, and the git checkout it ran in. "
        + "Read more of the log with get_log.")]
    public async Task<CallToolResult> RunScript(McpServer server,
        [Description(DirectoryHelp)] string directory,
        [Description("The id from list_scripts.")] string id,
        [Description("Parameter values by parameter name; a multichoice takes an array. Unset parameters use their defaults.")] JsonObject? values = null,
        [Description("True returns only the result object; false also returns the run's whole log.")] bool errorsOnly = true,
        [Description("Confirms an entry that asks for confirmation before it runs.")] bool confirm = false)
    {
        var agent = server.ClientInfo?.Name is { Length: > 0 } name ? name : UnnamedAgent;
        var result = await InvokeAsync(directory, settings => new CliCommand(CliVerb.Run)
        {
            Target = id,
            JsonValues = values?.ToDictionary(p => p.Key, p => p.Value) ?? [],
            Json = true,
            Yes = confirm,
            Agent = agent,
            AllowedIds = settings.Mcp?.AllowIds,
        });
        if (!errorsOnly && result.IsError != true && LogOf(result) is { } log)
            result.Content.Add(new TextContentBlock { Text = log });
        return result;
    }

    [McpServerTool(Name = "get_log", ReadOnly = true)]
    [Description("Reads a recorded run's log by its runId: all of it, the last lines, only the error lines, or a range of lines.")]
    public Task<CallToolResult> GetLog(
        [Description("The runId from run_script.")] string runId,
        [Description("Returns only the last this many lines.")] int? tail = null,
        [Description("Returns only the error lines.")] bool errorsOnly = false,
        [Description("The first line to return, counting from 1.")] int? fromLine = null,
        [Description("The last line to return.")] int? toLine = null)
    {
        if (tail <= 0 || fromLine <= 0 || toLine < (fromLine ?? 1))
            throw new McpException("tail and fromLine must be positive, and toLine at least fromLine.");
        if (HistoryStore.Containing(paths, runId) is not { } store)
            return Task.FromResult(Error($"No recorded run '{runId}'."));
        var output = new StringWriter();
        var error = new StringWriter();
        var command = new CliCommand(CliVerb.Log) { Target = runId, Tail = tail, ErrorsOnly = errorsOnly, FirstLine = fromLine, LastLine = toLine };
        var exitCode = new CliRunner(paths, App.LoadSettings(paths, error), output, error).ShowLog(command, store);
        return Task.FromResult(ResultOf(exitCode, output, error));
    }

    [McpServerTool(Name = "get_stats", ReadOnly = true)]
    [Description("The run statistics for a period: time per script, folder, trigger and git checkout, repeated runs, the slowest and the "
        + "flaky tests.")]
    public Task<CallToolResult> GetStats(
        [Description(DirectoryHelp)] string directory,
        [Description("The period: 1d, 7d, 30d, 12h and so on.")] string since = "7d")
    {
        if (!RunStats.TryParsePeriod(since, out var period))
            throw new McpException($"since expects a period such as 1d, 7d, 30d or 12h, not '{since}'.");
        return InvokeAsync(directory, _ => new CliCommand(CliVerb.Stats) { Since = period, Json = true },
            (output, checkout) =>
            {
                var stats = output.AsObject();
                stats["checkout"] = checkout;
                return stats;
            });
    }

    private async Task<CallToolResult> InvokeAsync(string directory, Func<Settings, CliCommand> commandFor,
        Func<JsonNode, JsonNode?, JsonNode>? withCheckout = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new McpException($"directory must be the absolute path of your working folder, not '{directory}'.");
        if (!Directory.Exists(directory))
            throw new McpException($"The directory '{directory}' does not exist.");
        var output = new StringWriter();
        var error = new StringWriter();
        var settings = App.LoadSettings(paths, error);
        var runner = new CliRunner(paths, settings, output, error);
        var exitCode = await runner.RunAsync(commandFor(settings), directory, trustedOnly: true);
        if (exitCode == 0 && withCheckout is not null && JsonNode.Parse(output.ToString()) is { } json)
        {
            var checkout = JsonSerializer.SerializeToNode(Checkout.Read(directory), RunResultJson.Options);
            output = new StringWriter();
            output.Write(withCheckout(json, checkout).ToJsonString(RunResultJson.Options));
        }
        return ResultOf(exitCode, output, error);
    }

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

    private static string? LogOf(CallToolResult result)
    {
        try
        {
            return result.Content.FirstOrDefault() is TextContentBlock { Text: var json }
                && JsonNode.Parse(json)?["logPath"]?.GetValue<string>() is { } path && File.Exists(path)
                ? File.ReadAllText(path)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
