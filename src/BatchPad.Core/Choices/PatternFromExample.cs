using System.Text.RegularExpressions;

namespace BatchPad.Core.Choices;

/// <summary>A <c>regex</c> choice source's settings: group 1 of the first match (or every match when <see cref="All"/>), split on <see cref="Split"/>.</summary>
public sealed record DerivedPattern(string Regex, string? Split, bool All);

/// <summary>Derives a regex from one example value selected in a line of a file (§5.1).</summary>
public static class PatternFromExample
{
    private const string Separators = " ,;|";

    /// <summary>
    /// The text before the example anchors a pattern for every similar line when <paramref name="fileLines"/> has several;
    /// otherwise an example inside a separated list (<c>APPS=Alpha Beta Gamma</c>) gives the whole list, split.
    /// </summary>
    public static DerivedPattern? Derive(string line, int start, int length, IEnumerable<string>? fileLines = null)
    {
        if (start < 0 || length <= 0 || start + length > line.Length)
            return null;
        var example = line.Substring(start, length).Trim();
        if (example.Length == 0)
            return null;
        start = line.IndexOf(example, start, StringComparison.Ordinal);
        var end = start + example.Length;
        var token = TokenClass(example);
        var similarLines = new DerivedPattern(Escape(Context(line[..start])) + $"({token})", null, true);
        var regex = new Regex(similarLines.Regex, RegexOptions.CultureInvariant);
        if (fileLines?.Count(regex.IsMatch) > 1)
            return similarLines;

        if (ListSeparator(line, start, end) is { } separator)
        {
            bool InList(char c) => c == separator || IsTokenChar(c, token);
            var from = start;
            while (from > 0 && InList(line[from - 1]))
                from--;
            var to = end;
            while (to < line.Length && InList(line[to]))
                to++;
            while (from < start && line[from] == separator)
                from++;
            var capture = to < line.Length ? $@"([^{EscapeInClass(line[to])}\r\n]*)" : @"([^\r\n]*)";
            return new DerivedPattern(Escape(Context(line[..from])) + capture, separator.ToString(), false);
        }

        return similarLines;
    }

    private static char? ListSeparator(string line, int start, int end)
    {
        foreach (var separator in Separators)
        {
            var before = start >= 2 && line[start - 1] == separator && IsTokenChar(line[start - 2], null);
            var after = end + 1 < line.Length && line[end] == separator && IsTokenChar(line[end + 1], null);
            if (before || after)
                return separator;
        }
        return null;
    }

    /// <summary>The text before the value from the start of its last word, so similar lines with other leading text still match.</summary>
    private static string Context(string prefix)
    {
        var trimmed = prefix.TrimStart();
        var core = trimmed.TrimEnd();
        var lastSpace = core.LastIndexOfAny([' ', '\t']);
        return lastSpace < 0 ? trimmed : trimmed[(lastSpace + 1)..];
    }

    private static string TokenClass(string example) =>
        example.All(c => char.IsLetterOrDigit(c) || c == '_') ? @"\w+"
        : example.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-') ? @"[\w.-]+"
        : @"[^\s""')\],;]+";

    private static bool IsTokenChar(char c, string? token) => token switch
    {
        @"\w+" => char.IsLetterOrDigit(c) || c == '_',
        _ => char.IsLetterOrDigit(c) || c is '_' or '.' or '-',
    };

    private static string Escape(string text) => Regex.Escape(text).Replace("\\ ", " ").Replace("\\#", "#");

    private static string EscapeInClass(char c) => c is '\\' or ']' or '^' or '-' ? "\\" + c : c.ToString();
}
