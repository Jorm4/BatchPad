using System.Text;

namespace BatchPad.Core.Running;

/// <summary>Escaping for text that passes through a <c>cmd.exe /v:off /c</c> command line (§4 Quoting).</summary>
public static class CmdEscaper
{
    // %cd:~,% expands to nothing, which splits any %name% so cmd cannot expand it; caret cannot, inside quotes.
    private const string LiteralPercent = "%%cd:~,%";

    /// <summary>
    /// Quotes an argument the way a batch file's <c>%~1</c> reads it back. Batch files have no escape
    /// for an embedded quote, so one arrives doubled.
    /// </summary>
    public static string QuoteBatchArgument(string argument)
    {
        if (argument.AsSpan().IndexOfAny("\r\n\0") >= 0)
            throw new RunException("An argument for cmd.exe cannot contain a line break.");

        var needsQuotes = argument.Length == 0 || argument.AsSpan().IndexOfAny(" \t,;=&|<>^()\"") >= 0;
        return needsQuotes ? "\"" + argument.Replace("\"", "\"\"") + "\"" : argument;
    }

    /// <summary>Makes cmd pass <paramref name="commandLine"/> on literally, tracking cmd's own view of quoting.</summary>
    public static string Escape(string commandLine)
    {
        var escaped = new StringBuilder(commandLine.Length + 8);
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    escaped.Append(c);
                    break;
                case '%':
                    escaped.Append(LiteralPercent);
                    break;
                case '^' or '&' or '|' or '<' or '>' or '(' or ')' or '!' when !inQuotes:
                    escaped.Append('^').Append(c);
                    break;
                default:
                    escaped.Append(c);
                    break;
            }
        }
        return escaped.ToString();
    }

    public static string EscapeBatchArgument(string argument) => Escape(QuoteBatchArgument(argument));
}
