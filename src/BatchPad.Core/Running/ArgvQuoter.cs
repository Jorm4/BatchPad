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

    /// <summary>Splits a command line the way <c>CommandLineToArgvW</c> does, undoing <see cref="Join"/>.</summary>
    public static List<string> Split(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var inArgument = false;
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (inArgument)
                    arguments.Add(current.ToString());
                current.Clear();
                inArgument = false;
                continue;
            }
            inArgument = true;
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c != '\\')
                current.Append(c);
            else
            {
                var backslashes = 1;
                while (i + 1 < commandLine.Length && commandLine[i + 1] == '\\')
                {
                    backslashes++;
                    i++;
                }
                var beforeQuote = i + 1 < commandLine.Length && commandLine[i + 1] == '"';
                current.Append('\\', beforeQuote ? backslashes / 2 : backslashes);
                if (beforeQuote && backslashes % 2 == 1)
                {
                    current.Append('"');
                    i++;
                }
            }
        }
        if (inArgument)
            arguments.Add(current.ToString());
        return arguments;
    }
}
