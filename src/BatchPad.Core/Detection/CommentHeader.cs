using System.Text.RegularExpressions;

namespace BatchPad.Core.Detection;

/// <summary>Extracts a script's leading comment block, per comment syntax, as plain text lines.</summary>
internal static partial class CommentHeader
{
    public static List<string> Read(string extension, string[] lines) => extension switch
    {
        ".bat" or ".cmd" => LineComments(lines, BatchComment(), IsBatchPreamble),
        ".py" => PythonDocstring(lines) ?? LineComments(lines, HashComment(), IsHashPreamble),
        ".ps1" => PowerShellSynopsis(lines) ?? LineComments(lines, HashComment(), IsHashPreamble),
        ".cs" => [.. LineComments(lines, SlashComment(), IsCsPreamble).Select(l => XmlTag().Replace(l, "").Trim())],
        _ => [],
    };

    public static IEnumerable<string> BatchCommentLines(IEnumerable<string> lines) =>
        lines.Select(l => BatchComment().Match(l)).Where(m => m.Success).Select(m => m.Groups["text"].Value.TrimEnd());

    /// <summary>The first paragraph, stopping before a usage section.</summary>
    public static string? Description(IEnumerable<string> header)
    {
        var paragraph = header
            .SkipWhile(string.IsNullOrWhiteSpace)
            .TakeWhile(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("usage", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Trim());
        var text = string.Join(" ", paragraph);
        return text.Length > 0 ? text : null;
    }

    private static List<string> LineComments(string[] lines, Regex comment, Func<string, bool> isPreamble)
    {
        var block = new List<string>();
        foreach (var line in lines)
        {
            if (block.Count == 0 && isPreamble(line))
                continue;
            var match = comment.Match(line);
            if (!match.Success)
                break;
            block.Add(match.Groups["text"].Value.TrimEnd());
        }
        return block;
    }

    private static List<string>? PythonDocstring(string[] lines)
    {
        var start = Array.FindIndex(lines, l => !IsHashPreamble(l) && !HashComment().IsMatch(l));
        if (start < 0 || DocstringOpen().Match(lines[start]) is not { Success: true } open)
            return null;

        var quote = open.Groups["quote"].Value;
        var text = lines[start][(open.Index + open.Length)..];
        var result = new List<string>();
        for (var i = start; ;)
        {
            var end = text.IndexOf(quote, StringComparison.Ordinal);
            if (end >= 0)
            {
                result.Add(text[..end]);
                return result;
            }
            result.Add(text);
            if (++i >= lines.Length)
                return result;
            text = lines[i];
        }
    }

    private static List<string>? PowerShellSynopsis(string[] lines)
    {
        var synopsis = Array.FindIndex(lines, l => l.Trim().Equals(".SYNOPSIS", StringComparison.OrdinalIgnoreCase));
        if (synopsis < 0)
            return null;
        return [.. lines.Skip(synopsis + 1)
            .TakeWhile(l => !l.TrimStart().StartsWith('.') && !l.Contains("#>"))
            .Select(l => l.Trim())];
    }

    private static bool IsBatchPreamble(string line) =>
        string.IsNullOrWhiteSpace(line) || BatchPreamble().IsMatch(line);

    private static bool IsHashPreamble(string line) =>
        string.IsNullOrWhiteSpace(line) || line.StartsWith("#!") || CodingLine().IsMatch(line);

    private static bool IsCsPreamble(string line) =>
        string.IsNullOrWhiteSpace(line) || line.StartsWith("#!") || line.StartsWith("#:");

    [GeneratedRegex(@"^\s*@?(?:rem(?=\s|$)|::)\s?(?<text>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex BatchComment();

    [GeneratedRegex(@"^\s*@?(?:echo\s+off|setlocal\b.*)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex BatchPreamble();

    [GeneratedRegex(@"^\s*#(?![!])\s?(?<text>.*)$")]
    private static partial Regex HashComment();

    [GeneratedRegex(@"^#.*coding[:=]")]
    private static partial Regex CodingLine();

    [GeneratedRegex(@"^\s*///?\s?(?<text>.*)$")]
    private static partial Regex SlashComment();

    [GeneratedRegex(@"</?\w+[^>]*>")]
    private static partial Regex XmlTag();

    [GeneratedRegex(@"^\s*[rRuU]?(?<quote>""""""|''')")]
    private static partial Regex DocstringOpen();
}
