using BatchPad.App.ViewModels;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Tests;

/// <summary>A private data folder and, optionally, a private copy of <c>samples/demo</c>.</summary>
internal sealed class TestWorkspace : IDisposable
{
    public static string DemoSource => Path.Combine(AppContext.BaseDirectory, "samples", "demo");

    public string Root { get; } = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "BatchPadAppTests", Guid.NewGuid().ToString("N"))).FullName;

    public AppPaths Paths => new(Path.Combine(Root, "data"), isPortable: true);

    public string CopyDemo()
    {
        var target = Path.Combine(Root, "demo");
        foreach (var file in Directory.EnumerateFiles(DemoSource, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(DemoSource, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
        return target;
    }

    public MainViewModel OpenMain(string workspaceDirectory, bool trusted = false)
    {
        var main = new MainViewModel(Paths, new Settings());
        if (trusted)
            main.Trust.Trust(workspaceDirectory);
        main.OpenInitial(workspaceDirectory, Root);
        return main;
    }

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
