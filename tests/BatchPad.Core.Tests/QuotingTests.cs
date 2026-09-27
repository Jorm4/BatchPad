using System.Diagnostics;
using System.Text.Json;
using BatchPad.Core.Model;
using BatchPad.Core.Running;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class QuotingTests
{
    private static readonly string[] Awkward =
    [
        "plain", "has space", "", "^Smoke", "--benchmark_filter=^Smoke", "a&b", "x | y", "<in>", "(paren)",
        "50%", "%PATH%", "%%", "!bang!", "a!PATH!b", @"C:\dir with space\", @"C:\dir\", "a,b;c=d", @"back\\slash",
    ];

    private static readonly RunnerResolver Resolver = new(new InterpreterLocator());

    [TestMethod]
    public void DisplayRelativeToShortensWorkspacePathsAndNamesOtherPrograms()
    {
        var python = new CommandLine(@"C:\Users\someone\Python\py.exe", @"-3 -u C:\Work\Demo\tools\run.py --out ""C:\Work\Demo\out dir"" --log D:\elsewhere\x.log", @"C:\Work\Demo");
        Assert.AreEqual(@"py -3 -u tools\run.py --out ""out dir"" --log D:\elsewhere\x.log", python.DisplayRelativeTo(@"C:\work\demo\"));

        var builtExe = new CommandLine(@"C:\Work\Demo\build\release\bin\game.exe", "", @"C:\Work\Demo\build\release\bin");
        Assert.AreEqual(@"build\release\bin\game.exe", builtExe.DisplayRelativeTo(@"C:\Work\Demo"));
    }

    [TestMethod]
    public void BatchArgumentsArriveIntact()
    {
        var lines = Run(Script("echo_args.bat"), [.. Awkward, "--end"]);
        CollectionAssert.AreEqual(Awkward, lines);
    }

    [TestMethod]
    public void AQuoteInABatchArgumentArrivesDoubledAndDoesNotBreakTheRest()
    {
        var lines = Run(Script("echo_args.bat"), ["say \"hi\" & bye", "after", "--end"]);
        CollectionAssert.AreEqual(new[] { "say \"\"hi\"\" & bye", "after" }, lines);
    }

    [TestMethod]
    public void PythonArgumentsArriveIntact()
    {
        string[] arguments = [.. Awkward, "quo\"te", "\"quoted\"", "ends\\\"", @"\\server\share\"];
        var json = Run(Script("echo_args.py"), arguments).Single();
        CollectionAssert.AreEqual(arguments, JsonSerializer.Deserialize<string[]>(json));
    }

    [TestMethod]
    public void ArgvQuoterFollowsCommandLineToArgvRules()
    {
        Assert.AreEqual("plain", ArgvQuoter.Quote("plain"));
        Assert.AreEqual("\"\"", ArgvQuoter.Quote(""));
        Assert.AreEqual("\"a b\\\\\"", ArgvQuoter.Quote(@"a b\"));
        Assert.AreEqual("\"a\\\\\\\"b\"", ArgvQuoter.Quote("a\\\"b"));
        Assert.AreEqual(@"a\b", ArgvQuoter.Quote(@"a\b"));
    }

    [TestMethod]
    public void ArgvQuoterSplitsWhatItJoined()
    {
        string[] arguments = ["plain", "", @"a b\", "a\\\"b", @"C:\My Work\batchpad.json", @"a\\b", "\"", "tab\there"];

        CollectionAssert.AreEqual(arguments, ArgvQuoter.Split(ArgvQuoter.Join(arguments)));
        CollectionAssert.AreEqual(new[] { "a", "b c", "d" }, ArgvQuoter.Split("  a \"b c\"   d "));
    }

    [TestMethod]
    public void PowerShellFallsBackToWindowsPowerShellWithoutPwsh()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Path("hello.ps1"), "");
        var locator = new InterpreterLocator(environment: name => name switch
        {
            "SystemRoot" => @"C:\Windows",
            "PATH" => dir.Root,
            "ProgramFiles" => dir.Root,
            _ => null,
        });

        var command = new RunnerResolver(locator).Resolve(new ScriptNode { Path = "hello.ps1" }, ["a b"], dir.Root);

        Assert.AreEqual(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", command.FileName);
        Assert.AreEqual($"-NoProfile -ExecutionPolicy Bypass -File {ArgvQuoter.Quote(dir.Path("hello.ps1"))} \"a b\"", command.Arguments);
    }

    [TestMethod]
    public void RunnerIsPickedByExtensionUnlessSet()
    {
        Assert.AreEqual(Runner.Batch, RunnerResolver.EffectiveRunner(new ScriptNode { Path = "build.CMD" }));
        Assert.AreEqual(Runner.Python, RunnerResolver.EffectiveRunner(new ScriptNode { Module = "pytest" }));
        Assert.AreEqual(Runner.Shell, RunnerResolver.EffectiveRunner(new ScriptNode { Command = "git fetch" }));
        Assert.AreEqual(Runner.Exe, RunnerResolver.EffectiveRunner(new ScriptNode { Path = "x.py", Runner = Runner.Exe }));
    }

    private static ScriptNode Script(string fileName) => new() { Path = Fixtures.Path("run", fileName) };

    private static List<string> Run(ScriptNode script, IReadOnlyList<string> arguments)
    {
        var startInfo = Resolver.Resolve(script, arguments, Fixtures.Path("run")).ToStartInfo();
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();
        var stderr = process.StandardError.ReadToEndAsync();
        var lines = new List<string>();
        while (process.StandardOutput.ReadLine() is { } line)
            lines.Add(line);
        if (!process.WaitForExit(10_000))
            process.Kill(entireProcessTree: true);
        Assert.AreEqual(0, process.ExitCode, stderr.Result);
        return lines;
    }
}
