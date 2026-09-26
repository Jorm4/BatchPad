using System.Diagnostics;
using BatchPad.Core.Running;

namespace BatchPad.Core.Tests;

[TestClass]
public sealed class CapturedProcessTests
{
    [TestMethod]
    public void AGrandchildHoldingTheOutputOpenDoesNotExtendTheTimeout()
    {
        var cmd = new InterpreterLocator().Cmd;
        var line = new CommandLine(cmd, "/d /c \"start /b ping -n 9 127.0.0.1 >nul & echo done\"", Path.GetTempPath());
        var elapsed = Stopwatch.StartNew();

        var output = CapturedProcess.Run(line, TimeSpan.FromSeconds(1));

        Assert.IsLessThan(TimeSpan.FromSeconds(1.7), elapsed.Elapsed);
        Assert.AreEqual(0, output.ExitCode);
        CollectionAssert.Contains(output.Lines.ToList(), "done");
    }
}
