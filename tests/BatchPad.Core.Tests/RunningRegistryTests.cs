using System.Diagnostics;
using BatchPad.Core.Running;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class RunningRegistryTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);
    private const string WorkspaceFile = @"C:\repo\batchpad.json";

    [TestMethod]
    public async Task ALiveProcessIsAdoptedByTheNextSessionAndStopKillsIt()
    {
        using var dir = new TempDir();
        using var sleeper = StartSleeper(out var pythonId);
        try
        {
            var file = dir.Path("running.json");
            Assert.IsNotNull(new RunningRegistry(file).Add(Entry(sleeper.Id)));

            var nextSession = new RunningRegistry(file);
            var adopted = nextSession.Adopt(WorkspaceFile).Single();
            Assert.AreEqual(sleeper.Id, adopted.ProcessId);
            Assert.AreEqual("Serve", adopted.Entry.Name);
            Assert.IsEmpty(nextSession.Adopt(WorkspaceFile));
            Assert.IsEmpty(new RunningRegistry(file).Adopt(@"C:\other\batchpad.json"));

            await adopted.StopAsync(TimeSpan.Zero).WaitAsync(Limit);

            Assert.AreEqual(RunOutcome.Stopped, (await adopted.Completion).Outcome);
            Assert.IsTrue(sleeper.WaitForExit(Limit));
            Assert.IsTrue(Exited(pythonId));
            Assert.IsFalse(File.Exists(file));
        }
        finally
        {
            if (!sleeper.HasExited)
                sleeper.Kill(entireProcessTree: true);
        }
    }

    [TestMethod]
    public void StaleEntriesAreDropped()
    {
        using var dir = new TempDir();
        var file = dir.Path("running.json");
        using var current = Process.GetCurrentProcess();
        using var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        exited.WaitForExit();
        File.WriteAllText(file, $$"""
            [
              { "workspaceFile": "{{WorkspaceFile.Replace(@"\", @"\\")}}", "nodeKey": "k", "name": "Gone", "processId": {{exited.Id}},
                "processStartedUtc": "2020-01-01T00:00:00Z" },
              { "workspaceFile": "{{WorkspaceFile.Replace(@"\", @"\\")}}", "nodeKey": "k", "name": "Reused", "processId": {{current.Id}},
                "processStartedUtc": "2020-01-01T00:00:00Z" }
            ]
            """);

        Assert.IsEmpty(new RunningRegistry(file).Adopt(WorkspaceFile));
        Assert.IsFalse(File.Exists(file));
    }

    private static RunningEntry Entry(int processId) => new() { WorkspaceFile = WorkspaceFile, NodeKey = "Workspace:id:serve", Name = "Serve", ProcessId = processId };

    private static Process StartSleeper(out int pythonId)
    {
        var python = new InterpreterLocator().Python() ?? throw new AssertInconclusiveException("Python is not installed.");
        var startInfo = new ProcessStartInfo(python.Path) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (var argument in python.LeadingArguments.Append("-u").Append(Fixtures.Path("run", "sleep_forever.py")))
            startInfo.ArgumentList.Add(argument);
        var process = Process.Start(startInfo)!;
        pythonId = int.Parse(process.StandardOutput.ReadLine()!);
        return process;
    }

    private static bool Exited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.WaitForExit(Limit);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
