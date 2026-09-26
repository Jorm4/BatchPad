namespace BatchPad.Core.Running;

public sealed record Interpreter(string Path, IReadOnlyList<string> LeadingArguments);

/// <summary>
/// Finds interpreters by full path: settings overrides first, then known install locations and absolute
/// <c>PATH</c> entries. Never the workspace folder or the current directory (§4.3).
/// </summary>
public sealed class InterpreterLocator(
    IReadOnlyDictionary<string, string>? overrides = null, Func<string, string?>? environment = null)
{
    private readonly IReadOnlyDictionary<string, string> _overrides = overrides ?? new Dictionary<string, string>();
    private readonly Func<string, string?> _environment = environment ?? Environment.GetEnvironmentVariable;

    public string Cmd => Path.Combine(SystemDirectory, "cmd.exe");

    /// <summary><c>py -3</c>, else <c>python</c>; null when neither is installed.</summary>
    public Interpreter? Python()
    {
        if (Override("python") is { } python)
            return IsLauncher(python) ? new(python, ["-3"]) : new(python, []);

        var launcher = FirstExisting(
            Under("LOCALAPPDATA", @"Programs\Python\Launcher\py.exe"),
            Under("SystemRoot", "py.exe"),
            OnPath("py.exe"));
        if (launcher is not null)
            return new(launcher, ["-3"]);

        return OnPath("python.exe") is { } fallback ? new(fallback, []) : null;
    }

    public string PowerShell() =>
        Override("pwsh")
        ?? FirstExisting(OnPath("pwsh.exe"), Under("ProgramFiles", @"PowerShell\7\pwsh.exe"))
        ?? Path.Combine(SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    public string? Dotnet() =>
        Override("dotnet") ?? FirstExisting(OnPath("dotnet.exe"), Under("ProgramFiles", @"dotnet\dotnet.exe"));

    private string SystemDirectory => Path.Combine(_environment("SystemRoot") ?? @"C:\Windows", "System32");

    private static bool IsLauncher(string path) => Path.GetFileName(path).Equals("py.exe", StringComparison.OrdinalIgnoreCase);

    private string? Override(string name) =>
        _overrides.TryGetValue(name, out var path) && !string.IsNullOrWhiteSpace(path) ? path : null;

    private string? Under(string variable, string relativePath) =>
        _environment(variable) is { Length: > 0 } root ? Path.Combine(root, relativePath) : null;

    private string? OnPath(string fileName)
    {
        foreach (var directory in (_environment("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var unquoted = directory.Trim('"');
            if (!Path.IsPathFullyQualified(unquoted) || IsStoreAlias(unquoted, fileName))
                continue;
            var candidate = Path.Combine(unquoted, fileName);
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    // The Store's python.exe alias opens the Store instead of running anything.
    private static bool IsStoreAlias(string directory, string fileName) =>
        fileName == "python.exe" && directory.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase);

    private static string? FirstExisting(params string?[] candidates) =>
        candidates.FirstOrDefault(c => c is not null && File.Exists(c));
}
