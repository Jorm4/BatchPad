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

    // Below Windows' ephemeral range (49152+): a port from bind(0) is the next one the OS hands an outgoing connection.
    public static int FreePort()
    {
        while (true)
        {
            var listener = new TcpListener(IPAddress.Loopback, Random.Shared.Next(20000, 45000)) { ExclusiveAddressUse = true };
            try
            {
                listener.Start();
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
            }
            finally
            {
                listener.Stop();
            }
        }
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
