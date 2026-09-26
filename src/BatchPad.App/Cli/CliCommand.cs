namespace BatchPad.App.Cli;

public enum CliVerb { Run, List }

public sealed record CliCommand(CliVerb Verb)
{
    public string? Target { get; init; }
    public string? Workspace { get; init; }
    public IReadOnlyDictionary<string, string> Values { get; init; } = new Dictionary<string, string>();
    public bool Yes { get; init; }

    public const string Usage = """
        Usage:
          batchpad run <id|name> [--workspace <path>] [--set name=value]... [--yes]
          batchpad list [--workspace <path>]
        """;

    public static bool IsCli(IReadOnlyList<string> args) => args is ["run" or "list", ..];

    /// <exception cref="CliUsageException" />
    public static CliCommand Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !Enum.TryParse<CliVerb>(args[0], ignoreCase: true, out var verb))
            throw new CliUsageException("Expected 'run' or 'list'.");
        string? target = null, workspace = null;
        var yes = false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--workspace" or "-w":
                    workspace = ValueAfter(args, ref i);
                    break;
                case "--set" when verb == CliVerb.Run:
                    var assignment = ValueAfter(args, ref i);
                    var equals = assignment.IndexOf('=');
                    if (equals <= 0)
                        throw new CliUsageException($"--set expects name=value, not '{assignment}'.");
                    values[assignment[..equals]] = assignment[(equals + 1)..];
                    break;
                case "--yes" or "-y" when verb == CliVerb.Run:
                    yes = true;
                    break;
                case var option when option.StartsWith('-'):
                    throw new CliUsageException($"Unknown option '{option}'.");
                case var positional when verb == CliVerb.Run && target is null:
                    target = positional;
                    break;
                default:
                    throw new CliUsageException($"Unexpected argument '{args[i]}'.");
            }
        }
        if (verb == CliVerb.Run && target is null)
            throw new CliUsageException("'run' needs the id or name of a script or workflow.");
        return new CliCommand(verb) { Target = target, Workspace = workspace, Values = values, Yes = yes };
    }

    private static string ValueAfter(IReadOnlyList<string> args, ref int i) =>
        ++i < args.Count ? args[i] : throw new CliUsageException($"{args[i - 1]} needs a value.");
}

public sealed class CliUsageException(string message) : Exception(message);
