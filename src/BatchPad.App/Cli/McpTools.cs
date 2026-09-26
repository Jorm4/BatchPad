using System.ComponentModel;
using System.Text.Json.Nodes;
using BatchPad.Core.Telemetry;
using BatchPad.Core.Workspace;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BatchPad.App.Cli;

/// <summary>The MCP tools, each one a <see cref="CliRunner"/> verb run in-process with its output captured.</summary>
public sealed class McpTools(AppPaths paths, string workspaceFile)
{
    public const string UnnamedAgent = "mcp";

    [McpServerTool(Name = "list_scripts", ReadOnly = true)]
    [Description("Lists the scripts and workflows this workspace can run: id, name, folder, description and parameters (type, choices, default, required).")]
    public Task<CallToolResult> ListScripts() =>
        InvokeAsync(_ => new CliCommand(CliVerb.List) { Json = true });

    [McpServerTool(Name = "run_script")]
    [Description("Runs a script or workflow by id, waits for it, and returns one result object: outcome, exitCode, durationMs, queuedMs, "
        + "logPath, runId, the test summary, the error lines with their file and line, and the steps. Read more of the log with get_log.")]
    public async Task<CallToolResult> RunScript(McpServer server,
        [Description("The id from list_scripts.")] string id,
        [Description("Parameter values by parameter name; a multichoice takes an array. Unset parameters use their defaults.")] JsonObject? values = null,
        [Description("True returns only the result object; false also returns the run's whole log.")] bool errorsOnly = true,
        [Description("Confirms an entry that asks for confirmation before it runs.")] bool confirm = false)
    {
        var agent = server.ClientInfo?.Name is { Length: > 0 } name ? name : UnnamedAgent;
        var result = await InvokeAsync(settings => new CliCommand(CliVerb.Run)
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
        return InvokeAsync(_ => new CliCommand(CliVerb.Log)
        {
            Target = runId,
            Tail = tail,
            ErrorsOnly = errorsOnly,
            FirstLine = fromLine,
            LastLine = toLine,
        });
    }

    [McpServerTool(Name = "get_stats", ReadOnly = true)]
    [Description("The run statistics for a period: time per script, folder and trigger, repeated runs, the slowest and the flaky tests.")]
    public Task<CallToolResult> GetStats(
        [Description("The period: 1d, 7d, 30d, 12h and so on.")] string since = "7d")
    {
        if (!RunStats.TryParsePeriod(since, out var period))
            throw new McpException($"since expects a period such as 1d, 7d, 30d or 12h, not '{since}'.");
        return InvokeAsync(_ => new CliCommand(CliVerb.Stats) { Since = period, Json = true });
    }

    private async Task<CallToolResult> InvokeAsync(Func<Settings, CliCommand> commandFor)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var settings = App.LoadSettings(paths, error);
        var runner = new CliRunner(paths, settings, output, error);
        var exitCode = await runner.RunAsync(commandFor(settings) with { Workspace = workspaceFile },
            Path.GetDirectoryName(workspaceFile)!, trustedOnly: true);

        var text = output.ToString().TrimEnd();
        var problems = error.ToString().TrimEnd();
        if (exitCode != 0 && text.Length == 0)
            return new CallToolResult { IsError = true, Content = [Text(problems.Length > 0 ? problems : $"Failed with exit code {exitCode}.")] };
        var result = new CallToolResult { Content = [Text(text)] };
        if (problems.Length > 0)
            result.Content.Add(Text(problems));
        return result;
    }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
