using System.Text;

namespace BatchPad.Core.Running;

/// <summary>Quotes arguments so <c>CommandLineToArgvW</c> and the C runtime split them back unchanged.</summary>
public static class ArgvQuoter
{
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
            return argument;

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    public static string Join(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));
}
