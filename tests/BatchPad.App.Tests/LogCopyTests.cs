using BatchPad.App.ViewModels;
using BatchPad.App.Views;
using BatchPad.Core.Running;

namespace BatchPad.App.Tests;

[TestClass]
public sealed class LogCopyTests
{
    [TestMethod]
    public void SelectedLinesCopyInLogOrderWhateverOrderTheyWereChosen()
    {
        var lines = new[] { "build", "error C1083: one", "linking", "error C1083: two", "done" }
            .Select(t => new OutputLineViewModel(t, OutputStream.Stdout)).ToList();

        var text = LogCopy.TextOf(lines, [lines[3], lines[1]]);

        Assert.AreEqual($"error C1083: one{Environment.NewLine}error C1083: two", text);
    }

    [TestMethod]
    public void IdenticalLinesAreCopiedOnlyWhereSelected()
    {
        var lines = new[] { "same", "same", "same" }.Select(t => new OutputLineViewModel(t, OutputStream.Stdout)).ToList();

        Assert.AreEqual("same", LogCopy.TextOf(lines, [lines[1]]));
    }
}
