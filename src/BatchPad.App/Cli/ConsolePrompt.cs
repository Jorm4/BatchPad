using System.Text;

namespace BatchPad.App.Cli;

/// <summary>Asks the person at the console; injected so tests can answer instead.</summary>
public interface IConsolePrompt
{
    /// <summary>False when input is piped or redirected, so nobody can be asked.</summary>
    bool IsInteractive { get; }

    string? ReadLine();

    /// <summary>Reads a line without echoing it.</summary>
    string? ReadSecret();
}

public sealed class ConsolePrompt : IConsolePrompt
{
    public bool IsInteractive => !Console.IsInputRedirected;

    public string? ReadLine() => Console.ReadLine();

    public string? ReadSecret()
    {
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.Error.WriteLine();
                    return text.ToString();
                case ConsoleKey.Escape:
                    Console.Error.WriteLine();
                    return null;
                case ConsoleKey.Backspace:
                    if (text.Length > 0)
                        text.Length--;
                    break;
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                        text.Append(key.KeyChar);
                    break;
            }
        }
    }
}
