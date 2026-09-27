using BatchPad.App.Services;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class ErrorLogTests
{
    [TestMethod]
    public void ErrorsAreAppendedAndTheOldestHalfGoesPastTheCap()
    {
        using var dir = new TempDir();
        var log = new ErrorLog(dir.Path("local", "errors.log"));

        log.Append(new InvalidOperationException("first"));
        log.Append(new InvalidOperationException("second"));
        StringAssert.Contains(File.ReadAllText(log.Path), "first");
        StringAssert.Contains(File.ReadAllText(log.Path), "second");

        File.WriteAllText(log.Path, "old" + new string('x', 2 * 1024 * 1024));
        log.Append(new InvalidOperationException("latest"));
        var text = File.ReadAllText(log.Path);
        Assert.IsLessThan(2 * 1024 * 1024, text.Length);
        Assert.DoesNotContain("old", text);
        StringAssert.Contains(text, "latest");
    }
}
