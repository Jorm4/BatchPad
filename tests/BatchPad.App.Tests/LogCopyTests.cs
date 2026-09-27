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

    [TestMethod]
    public void DraggingSelectsTheRangeAndChangesOnlyWhatMoved()
    {
        var (remove, add) = DragSelection.Delta(null, (3, 5));
        Assert.IsEmpty(remove);
        CollectionAssert.AreEqual(new[] { 3, 4, 5 }, add.ToList());

        (remove, add) = DragSelection.Delta((3, 5), (3, 7));
        Assert.IsEmpty(remove);
        CollectionAssert.AreEqual(new[] { 6, 7 }, add.ToList());

        (remove, add) = DragSelection.Delta((3, 7), (1, 3));
        CollectionAssert.AreEqual(new[] { 4, 5, 6, 7 }, remove.ToList());
        CollectionAssert.AreEqual(new[] { 1, 2 }, add.ToList());
    }
}
