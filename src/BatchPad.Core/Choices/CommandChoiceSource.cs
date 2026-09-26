using System.Collections.Concurrent;
using BatchPad.Core.Model;
using BatchPad.Core.Running;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;

namespace BatchPad.Core.Choices;

/// <summary>
/// Runs a <c>command</c> choice source's script and takes one choice per non-empty output line (§3.3).
/// Results are cached until the script file changes or <see cref="Refresh"/> is called.
/// </summary>
public sealed class CommandChoiceSource(TrustStore trust, InterpreterLocator interpreters, TimeSpan? timeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private readonly ConcurrentDictionary<string, (DateTime Stamp, IReadOnlyList<string> Found)> _cache = new();

    public void Refresh() => _cache.Clear();

    /// <exception cref="ChoiceSourceException">The workspace is untrusted, or the script fails, times out or cannot start.</exception>
    public IReadOnlyList<string> Find(string command, ChoiceContext context)
    {
        if (!trust.IsTrusted(context.BaseDirectory))
            throw new ChoiceSourceException($"Choices from \"{command}\" need a trusted workspace; BatchPad runs nothing until you trust it.");

        var expanded = context.Templates is { } templates ? TemplateExpander.ExpandText(command, templates) : command;
        var scriptPath = Path.GetFullPath(Path.Combine(context.BaseDirectory, expanded));
        var isFile = File.Exists(scriptPath);
        var script = isFile ? new ScriptNode { Path = expanded } : new ScriptNode { Command = expanded };
        CommandLine line;
        try
        {
            line = new RunnerResolver(interpreters).Resolve(script, [], context.BaseDirectory, context.Templates);
        }
        catch (RunException ex)
        {
            throw new ChoiceSourceException(ex.Message);
        }

        var key = $"{line.Display}\n{line.WorkingDirectory}";
        var stamp = isFile ? File.GetLastWriteTimeUtc(scriptPath) : DateTime.MinValue;
        if (_cache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
            return cached.Found;
        var found = Run(command, line);
        _cache[key] = (stamp, found);
        return found;
    }

    private List<string> Run(string command, CommandLine line)
    {
        CapturedOutput output;
        try
        {
            output = CapturedProcess.Run(line, _timeout);
        }
        catch (Exception ex) when (ex is TimeoutException or RunException)
        {
            throw new ChoiceSourceException(ex is TimeoutException
                ? $"\"{command}\" did not finish within {_timeout.TotalSeconds:0.#} s."
                : ex.Message);
        }
        if (output.ExitCode != 0)
            throw new ChoiceSourceException($"\"{command}\" exited with code {output.ExitCode}"
                + (output.FirstError is { } error ? $": {error}" : "."));
        return output.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
    }
}
