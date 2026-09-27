using System.Text;
using System.Text.RegularExpressions;
using BatchPad.Core.Model;

namespace BatchPad.Core.Detection;

/// <summary>
/// Proposes batch parameters from header usage lines (<c>rem   build.bat [--release | --final] [--asan] [name ...]</c>),
/// then from <c>"%~1"=="--flag"</c> comparisons, and failing both from plain <c>%~1</c>…<c>%~9</c> use.
/// </summary>
internal static partial class BatchParameterDetector
{
    public static List<ParameterDefinition> Detect(string fileName, string[] lines)
    {
        var comments = CommentHeader.BatchCommentLines(lines).ToList();
        var proposals = new List<ParameterDefinition>();
        foreach (var usage in UsageLines(fileName, comments))
            foreach (var parameter in ParseUsage(usage))
                AddUnlessCovered(proposals, parameter);

        foreach (var flag in ComparedFlags(lines))
            AddUnlessCovered(proposals, Flag(flag));

        if (proposals.Count == 0)
            proposals.AddRange(PositionalFromArgumentUse(lines));

        DescribeOptions(proposals, comments);
        return proposals;
    }

    private static IEnumerable<string> UsageLines(string fileName, IEnumerable<string> comments)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        foreach (var comment in comments)
        {
            var text = UsagePrefix().Replace(comment, "").Trim();
            var firstSpace = text.IndexOf(' ');
            var command = firstSpace < 0 ? text : text[..firstSpace];
            if (command.Equals(fileName, StringComparison.OrdinalIgnoreCase) || command.Equals(stem, StringComparison.OrdinalIgnoreCase))
                yield return firstSpace < 0 ? "" : DescriptionGap().Split(text[firstSpace..], 2)[0].Trim();
        }
    }

    private static List<ParameterDefinition> ParseUsage(string usage)
    {
        var tokens = UsageToken().Matches(usage).Select(m => m.Value).ToList();
        var parameters = new List<ParameterDefinition>();
        for (var i = 0; i < tokens.Count; i++)
        {
            List<string> group;
            var optional = tokens[i] == "[";
            if (optional)
            {
                var start = i + 1;
                for (var depth = 1; depth > 0 && ++i < tokens.Count;)
                    depth += tokens[i] switch { "[" => 1, "]" => -1, _ => 0 };
                group = [.. tokens[start..Math.Min(i, tokens.Count)].Where(t => t is not "[" and not "]")];
            }
            else if (tokens[i] == "...")
            {
                if (parameters.LastOrDefault() is { Arg: null } last)
                    MakeList(last);
                continue;
            }
            else
            {
                group = [tokens[i]];
                if (IsOption(tokens[i]) && i + 1 < tokens.Count && IsValuePlaceholder(tokens[i + 1]))
                    group.Add(tokens[++i]);
            }

            if (ParseGroup(group, optional) is { } parameter)
                parameters.Add(parameter);
        }
        return parameters;
    }

    private static ParameterDefinition? ParseGroup(List<string> group, bool optional)
    {
        if (group.Count == 0)
            return null;

        if (group.Contains("|"))
        {
            var alternatives = string.Join(" ", group).Split('|', StringSplitOptions.TrimEntries);
            if (!alternatives.All(a => IsOption(a) && !a.Contains(' ')))
                return null;
            List<ChoiceDefinition> choices = optional ? [new ChoiceDefinition { Value = "", Label = "None" }] : [];
            choices.AddRange(alternatives.Select(a => new ChoiceDefinition { Value = a }));
            return new ParameterDefinition { Name = Identifier(alternatives[0]), Type = ParameterType.Choice, Choices = choices };
        }

        var first = group[0];
        if (IsOption(first))
        {
            var equals = first.IndexOf('=');
            if (equals > 0)
                return new ParameterDefinition { Name = Identifier(first[..equals]), Type = ParameterType.Text, Arg = first[..(equals + 1)] };
            return group.Count == 1
                ? Flag(first)
                : new ParameterDefinition { Name = Identifier(first), Type = ParameterType.Text, Arg = first };
        }

        // A bare word outside brackets is a sample value (build.bat SpaceTrader), not a placeholder.
        if (!optional && !IsValuePlaceholder(first) && !group.Any(t => t.Contains("...")))
            return null;
        var parameter = new ParameterDefinition
        {
            Name = Identifier(first),
            Type = ParameterType.Text,
            Required = optional ? null : true,
        };
        if (group.Any(t => t.Contains("...")))
            MakeList(parameter);
        return parameter;
    }

    private static void MakeList(ParameterDefinition parameter)
    {
        parameter.Split = true;
        parameter.Position = "end";
        parameter.Required = null;
    }

    private static ParameterDefinition Flag(string option) =>
        new() { Name = Identifier(option), Type = ParameterType.Flag, Arg = option };

    private static void AddUnlessCovered(List<ParameterDefinition> proposals, ParameterDefinition candidate)
    {
        var covered = proposals.Any(p =>
            p.Name == candidate.Name
            || (candidate.Arg is not null && (p.Arg == candidate.Arg || p.Choices?.Any(c => c.Value == candidate.Arg) == true)));
        if (!covered)
            proposals.Add(candidate);
    }

    private static IEnumerable<string> ComparedFlags(string[] lines) =>
        lines.SelectMany(l => FlagComparison().Matches(l)).Select(m => m.Groups["flag"].Value).Distinct(StringComparer.OrdinalIgnoreCase);

    private static List<ParameterDefinition> PositionalFromArgumentUse(string[] lines)
    {
        var highest = lines.SelectMany(l => ArgumentReference().Matches(l)).Select(m => m.Groups["n"].Value[0] - '0').DefaultIfEmpty(0).Max();
        return [.. Enumerable.Range(1, highest).Select(n => new ParameterDefinition { Name = $"arg{n}", Type = ParameterType.Text })];
    }

    private static void DescribeOptions(List<ParameterDefinition> proposals, List<string> comments)
    {
        foreach (var comment in comments)
        {
            var match = OptionDescription().Match(comment);
            if (!match.Success)
                continue;
            var option = match.Groups["opt"].Value;
            var parameter = proposals.FirstOrDefault(p => p.Arg?.TrimEnd('=') == option);
            if (parameter is not null)
                parameter.Description ??= match.Groups["desc"].Value.Trim();
        }
    }

    private static bool IsOption(string token) => token.StartsWith('-') && token.Length > 1 && token != "...";

    private static bool IsValuePlaceholder(string token) =>
        token.StartsWith('<') || (token.Any(char.IsLetter) && token.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_'));

    /// <summary><c>--no-logo</c> → <c>noLogo</c>, <c>&lt;name...&gt;</c> → <c>name</c>.</summary>
    private static string Identifier(string token)
    {
        var words = NonAlphanumeric().Split(token).Where(w => w.Length > 0).ToList();
        if (words.Count == 0)
            return "arg";
        var identifier = new StringBuilder(words[0].ToLowerInvariant());
        foreach (var word in words.Skip(1))
            identifier.Append(char.ToUpperInvariant(word[0])).Append(word[1..].ToLowerInvariant());
        return identifier.ToString();
    }

    [GeneratedRegex(@"^\s*usage\s*:?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex UsagePrefix();

    /// <summary>Two spaces or a tab: where a usage line's aligned description starts.</summary>
    [GeneratedRegex(@" {2,}|\t")]
    private static partial Regex DescriptionGap();

    [GeneratedRegex(@"\[|\]|\||<[^>]*>|\.\.\.|[^\s\[\]|<]+")]
    private static partial Regex UsageToken();

    [GeneratedRegex(@"""?%~?[1-9]""?\s*==\s*""?(?<flag>--?[\w-]+)|""?(?<flag>--?[\w-]+)""?\s*==\s*""?%~?[1-9]")]
    private static partial Regex FlagComparison();

    [GeneratedRegex(@"(?<!%)%~?(?<n>[1-9])")]
    private static partial Regex ArgumentReference();

    [GeneratedRegex(@"^\s*(?<opt>--?[\w-]+)(?:\s*[:=-]\s+|\s{2,})(?<desc>\S.*)$")]
    private static partial Regex OptionDescription();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();
}
