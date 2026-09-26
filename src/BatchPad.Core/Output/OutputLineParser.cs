using System.Text;
using System.Text.RegularExpressions;

namespace BatchPad.Core.Output;

/// <summary>The 16 ANSI colours in SGR order; <see cref="Default"/> leaves the line's own colour.</summary>
public enum AnsiColor : byte
{
    Default,
    Black, Red, Green, Yellow, Blue, Magenta, Cyan, White,
    BrightBlack, BrightRed, BrightGreen, BrightYellow, BrightBlue, BrightMagenta, BrightCyan, BrightWhite,
}

public readonly record struct OutputSpan(string Text, AnsiColor Color, bool Bold);

/// <param name="Text">The line without escape sequences.</param>
/// <param name="Spans">Styled runs making up <paramref name="Text"/>; null when the line has no styling.</param>
public sealed record ParsedLine(string Text, IReadOnlyList<OutputSpan>? Spans, bool IsErrorMatch);

/// <summary>Splits output lines into ANSI-styled spans and flags lines matching a script's <c>errorPatterns</c> (§4).</summary>
/// <remarks>Each line starts unstyled, so lines can be parsed independently and from any thread.</remarks>
public sealed class OutputLineParser
{
    private const char Escape = '\u001b';
    private readonly Regex[] _errorPatterns;

    public OutputLineParser(IEnumerable<string>? errorPatterns = null)
    {
        _errorPatterns = [.. (errorPatterns ?? []).Select(TryCreate).OfType<Regex>()];
    }

    public static OutputLineParser Plain { get; } = new();

    public ParsedLine Parse(string line)
    {
        var (text, spans) = line.Contains(Escape) ? Split(line) : (line, null);
        return new ParsedLine(text, spans, IsError(text));
    }

    private bool IsError(string text) => _errorPatterns.Any(pattern => Matches(pattern, text));

    private static bool Matches(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static (string, IReadOnlyList<OutputSpan>?) Split(string line)
    {
        var spans = new List<OutputSpan>();
        var plain = new StringBuilder(line.Length);
        var current = new StringBuilder();
        var color = AnsiColor.Default;
        var bold = false;
        var styled = false;

        var i = 0;
        while (i < line.Length)
        {
            if (line[i] != Escape)
            {
                current.Append(line[i++]);
                continue;
            }
            var (end, sgr) = SkipSequence(line, i);
            if (sgr is not null)
            {
                var (newColor, newBold) = ApplySgr(sgr, color, bold);
                if ((newColor, newBold) != (color, bold))
                {
                    Flush();
                    (color, bold) = (newColor, newBold);
                    styled |= color != AnsiColor.Default || bold;
                }
            }
            i = end;
        }
        Flush();
        return (plain.ToString(), styled ? spans : null);

        void Flush()
        {
            if (current.Length == 0)
                return;
            var text = current.ToString();
            spans.Add(new OutputSpan(text, color, bold));
            plain.Append(text);
            current.Clear();
        }
    }

    /// <summary>The index after the escape sequence at <paramref name="start"/>, and its SGR parameters when it is one.</summary>
    private static (int End, string? Sgr) SkipSequence(string line, int start)
    {
        var i = start + 1;
        if (i >= line.Length)
            return (i, null);
        switch (line[i])
        {
            case '[':
                var parameters = ++i;
                while (i < line.Length && line[i] is < '@' or > '~')
                    i++;
                if (i >= line.Length)
                    return (i, null);
                return (i + 1, line[i] == 'm' ? line[parameters..i] : null);
            case ']':
                while (++i < line.Length)
                {
                    if (line[i] == '\a')
                        return (i + 1, null);
                    if (line[i] == Escape && i + 1 < line.Length && line[i + 1] == '\\')
                        return (i + 2, null);
                }
                return (i, null);
            default:
                return (i + 1, null);
        }
    }

    private static (AnsiColor, bool) ApplySgr(string parameters, AnsiColor color, bool bold)
    {
        if (parameters.Length == 0)
            return (AnsiColor.Default, false);
        var codes = parameters.Split(';');
        for (var k = 0; k < codes.Length; k++)
        {
            if (!int.TryParse(codes[k], out var code))
                code = 0;
            switch (code)
            {
                case 0: (color, bold) = (AnsiColor.Default, false); break;
                case 1: bold = true; break;
                case 22: bold = false; break;
                case 39: color = AnsiColor.Default; break;
                case >= 30 and <= 37: color = (AnsiColor)(code - 30 + 1); break;
                case >= 90 and <= 97: color = (AnsiColor)(code - 90 + 9); break;
                case 38 or 48:
                    k += k + 1 < codes.Length && codes[k + 1] == "5" ? 2 : k + 1 < codes.Length && codes[k + 1] == "2" ? 4 : 0;
                    break;
            }
        }
        return (color, bold);
    }

    private static Regex? TryCreate(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
