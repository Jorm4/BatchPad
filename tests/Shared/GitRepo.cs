using System.Diagnostics;

namespace BatchPad.Tests;

/// <summary>A real git repository, committed in <see cref="Main"/>, with a linked worktree of it in <see cref="Worktree"/>.</summary>
internal sealed class GitRepo : IDisposable
{
    public const string WorktreeBranch = "feature";

    private readonly TempDir _dir = new();

    public GitRepo(IReadOnlyDictionary<string, string> files)
    {
        foreach (var (name, text) in files)
        {
            var path = Path.Combine(Main, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Directory.CreateDirectory(Main);
        Git(Main, "init", "-q", "-b", "main");
        Git(Main, "add", "-A");
        Git(Main, "commit", "-q", "-m", "init");
        Git(Main, "worktree", "add", "-q", "-b", WorktreeBranch, Worktree);
    }

    public const string WorkspaceId = "worktree-fixture";

    /// <summary>A workspace whose <c>which</c> script prints "main" in the main checkout and "worktree" in the worktree.</summary>
    public static GitRepo WithWhichScript()
    {
        var repo = new GitRepo(new Dictionary<string, string>
        {
            ["batchpad.json"] = $$"""{ "id": "{{WorkspaceId}}", "scripts": [ { "id": "which", "name": "Which", "path": "which.bat" } ] }""",
            ["which.bat"] = "@type \"%~dp0checkout.txt\"\r\n",
            ["checkout.txt"] = "main\r\n",
        });
        File.WriteAllText(Path.Combine(repo.Worktree, "checkout.txt"), "worktree\r\n");
        return repo;
    }

    public string Root => _dir.Root;
    public string Main => _dir.Path("repo");
    public string Worktree => _dir.Path("wt");

    public static string Git(string directory, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        foreach (var arg in (string[])["-c", "user.name=t", "-c", "user.email=t@example.com", "-c", "commit.gpgsign=false",
                     "-c", "core.hooksPath=NUL", "-c", "core.autocrlf=false", .. args])
            startInfo.ArgumentList.Add(arg);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error.Result}");
        return output.Result;
    }

    // git writes its object files read-only.
    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        _dir.Dispose();
    }
}
