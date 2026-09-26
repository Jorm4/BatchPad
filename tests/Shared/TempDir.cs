namespace BatchPad.Tests;

internal sealed class TempDir : IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "BatchPadTests", Guid.NewGuid().ToString("N"))).FullName;

    public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
