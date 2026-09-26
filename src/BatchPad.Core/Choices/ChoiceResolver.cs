using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BatchPad.Core.Discovery;
using BatchPad.Core.Model;
using BatchPad.Core.Templating;

namespace BatchPad.Core.Choices;

/// <param name="BaseDirectory">Where relative globs and files resolve from, normally the workspace directory.</param>
/// <param name="Templates">Expands <c>${…}</c> in <c>glob</c>, <c>file</c> and <c>relativeTo</c>.</param>
public sealed record ChoiceContext(string BaseDirectory)
{
    public IReadOnlyDictionary<string, List<string>>? Lists { get; init; }
    public TemplateContext? Templates { get; init; }

    /// <summary>Runs <c>command</c> sources; without it they add a problem instead.</summary>
    public CommandChoiceSource? Commands { get; init; }
}

/// <summary>A source that fails (missing file, bad regex) adds a problem and contributes no choices.</summary>
public sealed record ResolvedChoices(IReadOnlyList<ChoiceDefinition> Choices, IReadOnlyList<string> Problems);

/// <summary>Fixed choices followed by <c>choicesFrom</c> sources (§3.3), duplicates removed by value.</summary>
public sealed class ChoiceResolver
{
    private readonly ConcurrentDictionary<string, (DateTime Stamp, IReadOnlyList<string> Found)> fileCache = new();

    public ResolvedChoices Resolve(ParameterDefinition parameter, ChoiceContext context)
    {
        var choices = new List<ChoiceDefinition>(parameter.Choices ?? []);
        var values = choices.Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var source in parameter.ChoicesFrom ?? [])
        {
            try
            {
                var transform = source.ValueTransform ?? parameter.ValueTransform;
                foreach (var found in Find(source, context))
                {
                    var choice = ToChoice(found, transform);
                    if (values.Add(choice.Value))
                        choices.Add(choice);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or TemplateException or ChoiceSourceException)
            {
                problems.Add(e.Message);
            }
        }
        return new ResolvedChoices(choices, problems);
    }

    private static ChoiceDefinition ToChoice(string found, string? transform) => transform switch
    {
        null => new ChoiceDefinition { Value = found },
        "lower" => new ChoiceDefinition { Value = found.ToLowerInvariant(), Label = found == found.ToLowerInvariant() ? null : found },
        _ => throw new ChoiceSourceException($"Unknown valueTransform \"{transform}\"."),
    };

    private IEnumerable<string> Find(ChoiceSource source, ChoiceContext context)
    {
        if (source.Glob is not null)
            return FindByGlob(source, context);
        if (source.File is not null && source.Regex is not null)
            return FindByRegex(source, context);
        if (source.List is not null)
            return context.Lists?.GetValueOrDefault(source.List) ?? throw new ChoiceSourceException($"No list named \"{source.List}\".");
        if (source.Command is not null)
            return (context.Commands ?? throw new ChoiceSourceException($"Choices from \"{source.Command}\" are not available here."))
                .Find(source.Command, context);
        throw new ChoiceSourceException("A choice source needs glob, file and regex, list, or command.");
    }

    private static List<string> FindByGlob(ChoiceSource source, ChoiceContext context)
    {
        var pattern = Expand(source.Glob!, context).Replace('\\', '/');
        var segments = pattern.Split('/');
        var fixedSegments = segments[..^1].TakeWhile(s => s.IndexOfAny(['*', '?']) < 0).Count();
        var searchRoot = Path.GetFullPath(Path.Combine(context.BaseDirectory, string.Join('/', segments[..fixedSegments])));
        if (!Directory.Exists(searchRoot))
            return [];

        var remainder = string.Join('/', segments[fixedSegments..]);
        var recursive = remainder.Contains('/') || remainder.Contains("**");
        var match = source.Match is null ? null : new Regex(source.Match, RegexOptions.CultureInvariant);
        var relativeTo = source.RelativeTo is null
            ? context.BaseDirectory
            : Path.GetFullPath(Path.Combine(context.BaseDirectory, Expand(source.RelativeTo, context)));

        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(searchRoot, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
        {
            if (!Glob.IsMatch(remainder, Relative(searchRoot, file)))
                continue;
            if (match is not null)
            {
                var m = match.Match(Relative(context.BaseDirectory, file));
                if (m.Success)
                    found.Add(m.Groups.Count > 1 ? m.Groups[1].Value : m.Value);
            }
            else
                found.Add(source.Stem ? Path.GetFileNameWithoutExtension(file) : Relative(relativeTo, file));
        }
        return found.Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private IReadOnlyList<string> FindByRegex(ChoiceSource source, ChoiceContext context)
    {
        var path = Path.GetFullPath(Path.Combine(context.BaseDirectory, Expand(source.File!, context)));
        var stamp = File.GetLastWriteTimeUtc(path);
        var key = string.Join('\n', path, source.Regex, source.Split, source.All);
        if (fileCache.TryGetValue(key, out var cached) && cached.Stamp == stamp)
            return cached.Found;

        var content = File.ReadAllText(path);
        var matches = Regex.Matches(content, source.Regex!, RegexOptions.Multiline | RegexOptions.CultureInvariant)
            .Take(source.All ? int.MaxValue : 1);
        var found = matches
            .Select(m => m.Groups.Count > 1 ? m.Groups[1].Value : m.Value)
            .SelectMany(capture => source.Split is null
                ? [capture.Trim()]
                : capture.Split(source.Split, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(s => s.Length > 0)
            .ToList();
        fileCache[key] = (stamp, found);
        return found;
    }

    private static string Relative(string from, string file) => Path.GetRelativePath(from, file).Replace('\\', '/');

    private static string Expand(string text, ChoiceContext context) =>
        context.Templates is { } templates ? TemplateExpander.ExpandText(text, templates) : text;
}

public sealed class ChoiceSourceException(string message) : Exception(message);
