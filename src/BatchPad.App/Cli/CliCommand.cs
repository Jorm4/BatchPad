using System.Globalization;
using System.Text.Json.Nodes;
using BatchPad.Core.Telemetry;

namespace BatchPad.App.Cli;

public enum CliVerb { Run, List, Log, Stats, Mcp }

public sealed record CliCommand(CliVerb Verb)
{
    public string? Target { get; init; }
    public string? Workspace { get; init; }
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, JsonNode?> JsonValues { get; init; } = new Dictionary<string, JsonNode?>();
    public IReadOnlyCollection<string>? AllowedIds { get; init; }
    public bool Yes { get; init; }
    public bool Json { get; init; }
    public bool ErrorsOnly { get; init; }
    public bool NoWait { get; init; }
    public string? Agent { get; init; }
    public int? Tail { get; init; }
    public int? FirstLine { get; init; }
    public int? LastLine { get; init; }
    public TimeSpan Since { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Wraps list and stats JSON with the workspace's git checkout, as the MCP tools return them.</summary>
    public bool WithCheckout { get; init; }

    public const string Usage = """
        Usage:
          batchpad run <id|name> [--workspace <path>] [--set name=value]... [--yes]
                       [--json | --errors-only] [--no-wait] [--agent <name>]
          batchpad list [--workspace <path>] [--json]
          batchpad log <run-id> [--workspace <path>] [--tail N] [--errors]
          batchpad stats [--workspace <path>] [--since 1d|7d|30d] [--json]
          batchpad mcp
        """;

    public static bool IsCli(IReadOnlyList<string> args) => args is ["run" or "list" or "log" or "stats" or "mcp", ..];

    /// <exception cref="CliUsageException" />
    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        var verb = args switch
        {
            ["run", ..] => CliVerb.Run,
            ["list", ..] => CliVerb.List,
            ["log", ..] => CliVerb.Log,
            ["stats", ..] => CliVerb.Stats,
            ["mcp", ..] => CliVerb.Mcp,
            _ => throw new CliUsageException("Expected 'run', 'list', 'log', 'stats' or 'mcp'."),
        };
        var command = new CliCommand(verb);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            command = (args[i], verb) switch
            {
                ("--workspace" or "-w", CliVerb.Mcp) => throw new CliUsageException(
                    "'batchpad mcp' has no workspace of its own: each tool call passes its 'directory', so an agent in a worktree runs that worktree's scripts."),
                ("--workspace" or "-w", _) => command with { Workspace = ValueAfter(args, ref i) },
                ("--set", CliVerb.Run) => Set(command, values, ValueAfter(args, ref i)),
                ("--yes" or "-y", CliVerb.Run) => command with { Yes = true },
                ("--json", CliVerb.Run or CliVerb.List or CliVerb.Stats) => command with { Json = true },
                ("--errors-only", CliVerb.Run) => command with { ErrorsOnly = true },
                ("--no-wait", CliVerb.Run) => command with { NoWait = true },
                ("--agent", CliVerb.Run) => command with { Agent = ValueAfter(args, ref i) },
                ("--tail", CliVerb.Log) => command with { Tail = Count(ValueAfter(args, ref i)) },
                ("--errors", CliVerb.Log) => command with { ErrorsOnly = true },
                ("--since", CliVerb.Stats) => command with { Since = ParsePeriod(ValueAfter(args, ref i), "--since", m => new CliUsageException(m)) },
                (var option, _) when option.StartsWith('-') => throw new CliUsageException($"Unknown option '{option}'."),
                (var positional, CliVerb.Run or CliVerb.Log) when command.Target is null => command with { Target = positional },
                _ => throw new CliUsageException($"Unexpected argument '{args[i]}'."),
            };
        }
        if (command.Target is null && verb is CliVerb.Run)
            throw new CliUsageException("'run' needs the id or name of a script or workflow.");
        if (command.Target is null && verb is CliVerb.Log)
            throw new CliUsageException("'log' needs a run id.");
        if (command.Json && command.ErrorsOnly)
            throw new CliUsageException("Use either --json or --errors-only.");
        return command with { Values = values };
    }

    private static CliCommand Set(CliCommand command, Dictionary<string, string> values, string assignment)
    {
        var equals = assignment.IndexOf('=');
        if (equals <= 0)
            throw new CliUsageException($"--set expects name=value, not '{assignment}'.");
        values[assignment[..equals]] = assignment[(equals + 1)..];
        return command;
    }

    private static int Count(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : throw new CliUsageException($"--tail expects a positive number, not '{text}'.");

    public static TimeSpan ParsePeriod(string text, string option, Func<string, Exception> error) =>
        RunStats.TryParsePeriod(text, out var period)
            ? period
            : throw error($"{option} expects a period such as 1d, 7d, 30d or 12h, not '{text}'.");

    private static string ValueAfter(IReadOnlyList<string> args, ref int i) =>
        ++i < args.Count ? args[i] : throw new CliUsageException($"{args[i - 1]} needs a value.");
}

public sealed class CliUsageException(string message) : Exception(message);
