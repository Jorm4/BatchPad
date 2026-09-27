using System.Globalization;
using System.Text.Json.Nodes;
using BatchPad.Core.Telemetry;

namespace BatchPad.App.Cli;

public enum CliVerb { Run, List, Log, Stats, Compare, Mcp, Trust, Untrust, Secret }

public sealed record CliCommand(CliVerb Verb)
{
    public string? Target { get; init; }

    /// <summary><c>run --schedule</c>: the key of the schedule to run, as its host would.</summary>
    public string? Schedule { get; init; }

    /// <summary><c>run --schedule --due</c>: the fire a Windows task was registered for, so a late one can be skipped.</summary>
    public DateTimeOffset? Due { get; init; }
    public string? Baseline { get; init; }
    public bool Cpu { get; init; }
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

    /// <summary><c>trust --list</c>: print the trusted folders instead of trusting one.</summary>
    public bool List { get; init; }

    /// <summary><c>secret</c>: the parameter to set or remove.</summary>
    public string? Parameter { get; init; }

    /// <summary><c>secret --global</c>: for Global scripts rather than the workspace's.</summary>
    public bool Global { get; init; }

    /// <summary>Wraps list and stats JSON with the workspace's git checkout, as the MCP tools return them.</summary>
    public bool WithCheckout { get; init; }

    public const string Usage = """
        Usage:
          batchpad run <id|name> [--workspace <path>] [--set name=value]... [--yes]
                       [--json | --errors-only] [--no-wait] [--agent <name>]
          batchpad run --schedule <key> [--workspace <path>] [--json | --errors-only]
          batchpad list [--workspace <path>] [--json]
          batchpad log <run-id> [--workspace <path>] [--tail N] [--errors]
          batchpad stats [--workspace <path>] [--since 1d|7d|30d] [--json]
          batchpad compare <run-id> [<baseline-run-id>] [--workspace <path>] [--cpu] [--json]
          batchpad mcp
          batchpad trust [--workspace <path>] | batchpad trust --list
          batchpad untrust [--workspace <path>]
          batchpad secret set|remove <parameter> [--workspace <path> | --global]
          batchpad secret list [--workspace <path> | --global]
        """;

    public static bool IsCli(IReadOnlyList<string> args) => args is ["run" or "list" or "log" or "stats" or "compare" or "mcp" or "trust" or "untrust" or "secret", ..];

    /// <exception cref="CliUsageException" />
    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        var verb = args switch
        {
            ["run", ..] => CliVerb.Run,
            ["list", ..] => CliVerb.List,
            ["log", ..] => CliVerb.Log,
            ["stats", ..] => CliVerb.Stats,
            ["compare", ..] => CliVerb.Compare,
            ["mcp", ..] => CliVerb.Mcp,
            ["trust", ..] => CliVerb.Trust,
            ["untrust", ..] => CliVerb.Untrust,
            ["secret", ..] => CliVerb.Secret,
            _ => throw new CliUsageException("Expected 'run', 'list', 'log', 'stats', 'compare', 'mcp', 'trust', 'untrust' or 'secret'."),
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
                ("--schedule", CliVerb.Run) => command with { Schedule = ValueAfter(args, ref i) },
                ("--due", CliVerb.Run) => command with { Due = Time(ValueAfter(args, ref i), "--due") },
                ("--set", CliVerb.Run) => Set(command, values, ValueAfter(args, ref i)),
                ("--yes" or "-y", CliVerb.Run) => command with { Yes = true },
                ("--json", CliVerb.Run or CliVerb.List or CliVerb.Stats or CliVerb.Compare) => command with { Json = true },
                ("--errors-only", CliVerb.Run) => command with { ErrorsOnly = true },
                ("--no-wait", CliVerb.Run) => command with { NoWait = true },
                ("--agent", CliVerb.Run or CliVerb.Trust or CliVerb.Secret) => command with { Agent = ValueAfter(args, ref i) },
                ("--list", CliVerb.Trust) => command with { List = true },
                ("--global", CliVerb.Secret) => command with { Global = true },
                ("--tail", CliVerb.Log) => command with { Tail = Count(ValueAfter(args, ref i)) },
                ("--errors", CliVerb.Log) => command with { ErrorsOnly = true },
                ("--cpu", CliVerb.Compare) => command with { Cpu = true },
                ("--since", CliVerb.Stats) => command with { Since = ParsePeriod(ValueAfter(args, ref i), "--since", m => new CliUsageException(m)) },
                (var option, _) when option.StartsWith('-') => throw new CliUsageException($"Unknown option '{option}'."),
                (var positional, CliVerb.Run or CliVerb.Log or CliVerb.Compare or CliVerb.Secret) when command.Target is null => command with { Target = positional },
                (var positional, CliVerb.Compare) when command.Baseline is null => command with { Baseline = positional },
                (var positional, CliVerb.Secret) when command.Parameter is null => command with { Parameter = positional },
                _ => throw new CliUsageException($"Unexpected argument '{args[i]}'."),
            };
        }
        if (command.Schedule is not null)
            CheckSchedule(command, values);
        else if (command.Due is not null)
            throw new CliUsageException("--due works only with --schedule.");
        else if (command.Target is null && verb is CliVerb.Run)
            throw new CliUsageException("'run' needs the id or name of a script or workflow.");
        if (command.Target is null && verb is CliVerb.Log or CliVerb.Compare)
            throw new CliUsageException($"'{args[0]}' needs a run id.");
        if (verb is CliVerb.Secret)
            CheckSecret(command);
        if (command.Json && command.ErrorsOnly)
            throw new CliUsageException("Use either --json or --errors-only.");
        return command with { Values = values };
    }

    private static void CheckSchedule(CliCommand command, Dictionary<string, string> values)
    {
        if (command.Target is not null)
            throw new CliUsageException("'run --schedule' runs the schedule's own target; drop the script or workflow.");
        if (values.Count > 0 || command.Yes)
            throw new CliUsageException("'run --schedule' uses the schedule's values and confirm rules; drop --set and --yes.");
    }

    private static void CheckSecret(CliCommand command)
    {
        if (command.Target is not ("set" or "remove" or "list"))
            throw new CliUsageException("'secret' needs 'set', 'remove' or 'list'.");
        if (command.Target != "list" && command.Parameter is null)
            throw new CliUsageException($"'secret {command.Target}' needs a parameter name.");
        if (command.Target == "list" && command.Parameter is not null)
            throw new CliUsageException($"Unexpected argument '{command.Parameter}'.");
        if (command.Global && command.Workspace is not null)
            throw new CliUsageException("Use either --workspace or --global.");
    }

    private static CliCommand Set(CliCommand command, Dictionary<string, string> values, string assignment)
    {
        var equals = assignment.IndexOf('=');
        if (equals <= 0)
            throw new CliUsageException($"--set expects name=value, not '{assignment}'.");
        values[assignment[..equals]] = assignment[(equals + 1)..];
        return command;
    }

    private static DateTimeOffset Time(string text, string option) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
            ? time
            : throw new CliUsageException($"{option} expects a date and time such as 2026-09-25T02:00:00+02:00, not '{text}'.");

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
