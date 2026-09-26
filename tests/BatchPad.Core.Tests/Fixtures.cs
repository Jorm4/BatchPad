namespace BatchPad.Core.Tests;

internal static class Fixtures
{
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([AppContext.BaseDirectory, "fixtures", .. parts]);

    public static string DemoWorkspace => System.IO.Path.Combine(AppContext.BaseDirectory, "samples", "demo");
}
