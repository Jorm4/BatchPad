using System.Text.RegularExpressions;
using BatchPad.Core.Trust;

namespace BatchPad.Core.Output;

/// <summary>A <c>file(line,col)</c> or <c>file:line:col</c> reference as written in an output line; <see cref="Column"/> is 0 when absent.</summary>
public readonly record struct SourceReference(int Start, int Length, string File, int Line, int Column);

public sealed record SourceLocation(string Path, int Line, int Column);

public static partial class SourceLocationParser
{
    [GeneratedRegex("""(?<file>(?:[A-Za-z]:)?[^\s:"'<>|*?()\[\],;=]+\.[A-Za-z0-9]+)(?:\((?<line>\d+)(?:,(?<col>\d+))?\)|:(?<line>\d+)(?::(?<col>\d+))?)""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, matchTimeoutMilliseconds: 100)]
    private static partial Regex Reference();

    /// <summary>The references in <paramref name="text"/>, or null when there are none. Doesn't touch the file system.</summary>
    public static IReadOnlyList<SourceReference>? Find(string text)
    {
        if (text.IndexOf(':') < 0 && text.IndexOf('(') < 0)
            return null;
        List<SourceReference>? found = null;
        try
        {
            for (var match = Reference().Match(text); match.Success; match = match.NextMatch())
            {
                if (!int.TryParse(match.Groups["line"].ValueSpan, out var line))
                    continue;
                int.TryParse(match.Groups["col"].ValueSpan, out var column);
                (found ??= []).Add(new SourceReference(match.Index, match.Length, match.Groups["file"].Value, line, column));
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return found;
    }

    internal static string? ExistingPath(string file, string workingDirectory)
    {
        if (LinkPolicy.IsNetworkOrDevicePath(file))
            return null;
        try
        {
            var path = Path.GetFullPath(Path.Combine(workingDirectory, file));
            return File.Exists(path) ? path : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
    }
}

/// <summary>Resolves references for one run, checking each distinct file once.</summary>
public sealed class SourceLocationResolver(Func<string> workingDirectory)
{
    private readonly Lazy<string> _workingDirectory = new(workingDirectory);
    private readonly Dictionary<string, string?> _paths = new(StringComparer.OrdinalIgnoreCase);

    public SourceLocation? Resolve(SourceReference reference)
    {
        string? path;
        lock (_paths)
        {
            if (!_paths.TryGetValue(reference.File, out path))
                _paths[reference.File] = path = SourceLocationParser.ExistingPath(reference.File, _workingDirectory.Value);
        }
        return path is null ? null : new SourceLocation(path, reference.Line, reference.Column);
    }
}

/// <summary>The command that opens a source location in the user's editor (<c>editorCommand</c> in settings, §3.8).</summary>
public static class EditorCommand
{
    public const string VsCode = "code -g \"{file}:{line}\"";
    public const string Notepad = "notepad.exe \"{file}\"";
    private const string FileVariable = "BP_FILE";

    /// <summary>The template to run, or null to let the shell open the file; Notepad for scripts and programs, which the shell would run.</summary>
    public static string? Template(string? configured, bool codeOnPath, string file) =>
        !string.IsNullOrWhiteSpace(configured) ? configured
        : codeOnPath ? VsCode
        : LinkPolicy.IsExecutable(file) ? Notepad
        : null;

    /// <summary>A command line for <c>cmd /v:on</c> with <see cref="Environment"/>: the path is read from a variable, so <c>&amp; | ^ %</c> in it stay inert.</summary>
    public static string Expand(string template, SourceLocation location) => template
        .Replace("{file}", $"!{FileVariable}!")
        .Replace("{line}", Math.Max(location.Line, 1).ToString(System.Globalization.CultureInfo.InvariantCulture))
        .Replace("{col}", Math.Max(location.Column, 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static IReadOnlyDictionary<string, string> Environment(SourceLocation location) =>
        new Dictionary<string, string> { [FileVariable] = location.Path };

    public static bool IsOnPath(string program) =>
        (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(folder => new[] { ".exe", ".cmd", ".bat" }.Any(extension => SourceLocationParser.ExistingPath(program + extension, folder) is not null));
}
