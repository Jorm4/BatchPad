namespace BatchPad.App.Tests;

[TestClass]
public sealed class SmokeTests
{
    [TestMethod]
    public void AppAssemblyIsNamedBatchPad()
    {
        Assert.AreEqual("BatchPad", typeof(App).Assembly.GetName().Name);
    }
}
