using System.Collections;

namespace BatchPad.Core.Running;

/// <summary>Layers environment variables (§4 Environment); names are case-insensitive, so <c>Path</c> and <c>PATH</c> are one.</summary>
public sealed class EnvironmentBuilder
{
    private readonly Dictionary<string, string> _variables = new(StringComparer.OrdinalIgnoreCase);

    public EnvironmentBuilder(IEnumerable<KeyValuePair<string, string>> initial)
    {
        foreach (var (name, value) in initial)
            _variables[name] = value;
    }

    public static EnvironmentBuilder FromCurrentProcess() =>
        new(Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .Select(e => KeyValuePair.Create((string)e.Key, (string?)e.Value ?? "")));

    /// <summary>Sets each variable; an empty value removes it.</summary>
    public EnvironmentBuilder Apply(IReadOnlyDictionary<string, string>? layer)
    {
        foreach (var (name, value) in layer ?? new Dictionary<string, string>())
        {
            if (value.Length == 0)
                _variables.Remove(name);
            else
                _variables[name] = value;
        }
        return this;
    }

    public EnvironmentBuilder ApplyFile(string? envFile) => envFile is null ? this : Apply(ReadEnvFile(envFile));

    public IReadOnlyDictionary<string, string> Build() => new Dictionary<string, string>(_variables, StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads a dotenv file: <c>NAME=value</c> lines, <c>#</c> comments, optional <c>export</c> and quotes.</summary>
    public static Dictionary<string, string> ReadEnvFile(string path)
    {
        if (!File.Exists(path))
            throw new RunException($"The envFile '{path}' was not found.");

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line["export ".Length..].TrimStart();
            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;
            var value = line[(equals + 1)..].Trim();
            if (value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0])
                value = value[1..^1];
            variables[line[..equals].Trim()] = value;
        }
        return variables;
    }
}
