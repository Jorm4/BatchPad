using System.Net;
using System.Net.Sockets;

namespace BatchPad.Tests;

internal static class TestHelpers
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public static async Task Eventually(Func<bool> condition, Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + Limit;
        while (!Holds(condition))
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail(describe?.Invoke() ?? "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    // The inline test dispatcher lets run threads change view-model collections while a test polls them.
    private static bool Holds(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }
}
