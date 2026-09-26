using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchPad.Core.Discovery;

/// <summary>
/// Case-insensitive globs over <c>/</c>-separated relative paths: <c>*</c> and <c>?</c> stay within a segment,
/// <c>**</c> crosses them. A pattern without <c>/</c> matches the file name alone.
/// </summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static bool IsMatch(string pattern, string relativePath)
    {
        pattern = pattern.Replace('\\', '/');
        relativePath = relativePath.Replace('\\', '/');
        var subject = pattern.Contains('/') ? relativePath : relativePath[(relativePath.LastIndexOf('/') + 1)..];
        return Cache.GetOrAdd(pattern, ToRegex).IsMatch(subject);
    }

    private static Regex ToRegex(string pattern)
    {
        var regex = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                regex.Append(followedBySlash ? "(.*/)?" : ".*");
                i += followedBySlash ? 2 : 1;
            }
            else if (c == '*')
                regex.Append("[^/]*");
            else if (c == '?')
                regex.Append("[^/]");
            else
                regex.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
