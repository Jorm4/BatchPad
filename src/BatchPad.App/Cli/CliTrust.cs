using BatchPad.Core.Trust;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Cli;

/// <summary><c>batchpad trust</c> and <c>untrust</c>: only a person at a terminal can trust a workspace (§4.5).</summary>
public sealed class CliTrust(TrustStore trust, IConsolePrompt prompt, TextWriter output, TextWriter error,
    Func<string, string?> environment)
{
    /// <summary>Set in <c>batchpad mcp</c>'s process, and so inherited by everything it runs.</summary>
    public const string McpVariable = "BATCHPAD_MCP";

    public int Trust(CliCommand command, string currentDirectory)
    {
        if (command.List)
        {
            foreach (var folder in trust.TrustedFolders)
                output.WriteLine(folder);
            return 0;
        }
        if (Refusal(command, "trust a workspace", prompt, environment) is { } refusal)
        {
            error.WriteLine(refusal);
            return CliRunner.Failure;
        }
        if (WorkspaceLocator.Locate(command.Workspace, currentDirectory, []) is not { } file || !File.Exists(file))
        {
            error.WriteLine("No batchpad.json found here or above; pass --workspace <path>.");
            return CliRunner.Failure;
        }
        var directory = Path.GetDirectoryName(file)!;
        if (trust.IsTrusted(directory))
        {
            output.WriteLine($"{directory} is already trusted.");
            return 0;
        }

        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) is { Length: > 0 } folderName ? folderName : directory;
        output.WriteLine($"Trust {directory}?");
        if (Checkout.OriginUrl(directory) is { } origin)
            output.WriteLine($"  origin: {origin}");
        output.WriteLine("BatchPad will then run its scripts, including from schedules, the command line and coding agents.");
        output.Write($"Type the folder name ({name}) to confirm: ");
        var answer = prompt.ReadLine()?.Trim();
        if (!string.Equals(answer, name, StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine($"Not trusted: '{answer}' is not '{name}'.");
            return CliRunner.Failure;
        }
        trust.Trust(directory);
        output.WriteLine($"Trusted {directory}.");
        return 0;
    }

    public int Untrust(CliCommand command, string currentDirectory)
    {
        var directory = command.Workspace is { } given
            ? FolderOf(Path.GetFullPath(given, currentDirectory))
            : WorkspaceLocator.FindUpwards(currentDirectory) is { } file ? Path.GetDirectoryName(file) : null;
        if (directory is null)
        {
            error.WriteLine("No batchpad.json found here or above; pass --workspace <path>.");
            return CliRunner.Failure;
        }
        if (trust.Revoke(directory))
        {
            output.WriteLine($"No longer trusted: {directory}.");
            return 0;
        }
        if (trust.IsTrusted(directory))
        {
            error.WriteLine($"{directory} is trusted through a folder above it or the repository it is a worktree of; untrust that instead.");
            return CliRunner.Failure;
        }
        output.WriteLine($"{directory} was not trusted.");
        return 0;
    }

    private static string FolderOf(string path) => File.Exists(path) ? Path.GetDirectoryName(path)! : path;

    /// <summary>Why <paramref name="action"/> can't go ahead: nobody is at the keyboard, or an agent asked for it.</summary>
    public static string? Refusal(CliCommand command, string action, IConsolePrompt prompt, Func<string, string?> environment)
    {
        if (IsAgent(command, environment))
            return $"Only a person can {action}, not a coding agent. Run this yourself in a terminal, or use the app.";
        if (!prompt.IsInteractive)
            return $"To {action}, BatchPad asks you to confirm at the keyboard, and this command's input is redirected. Run it in a terminal.";
        return null;
    }

    public static bool IsAgent(CliCommand command, Func<string, string?> environment) =>
        command.Agent is not null || environment("CLAUDECODE") == "1" || environment(McpVariable) == "1";
}
