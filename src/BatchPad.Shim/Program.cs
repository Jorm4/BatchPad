using System.Diagnostics;
using System.Text;

var exe = Path.Combine(AppContext.BaseDirectory, "BatchPad.exe");
if (!File.Exists(exe))
{
    Console.Error.WriteLine($"batchpad: {exe} not found; batchpad.com must sit beside BatchPad.exe.");
    return 1;
}

var startInfo = new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    StandardOutputEncoding = Encoding.UTF8,
    StandardErrorEncoding = Encoding.UTF8,
};
foreach (var arg in args)
    startInfo.ArgumentList.Add(arg);

using var process = Process.Start(startInfo)!;
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    try
    {
        process.Kill(entireProcessTree: true);
    }
    catch (InvalidOperationException)
    {
    }
};

var relays = Task.WhenAll(Relay(process.StandardOutput, Console.Out), Relay(process.StandardError, Console.Error));
await process.WaitForExitAsync();
// A process BatchPad left running may still hold the pipes open.
await Task.WhenAny(relays, Task.Delay(TimeSpan.FromSeconds(1)));
return process.ExitCode;

static async Task Relay(StreamReader from, TextWriter to)
{
    var buffer = new char[4096];
    int read;
    while ((read = await from.ReadAsync(buffer)) > 0)
    {
        to.Write(buffer, 0, read);
        to.Flush();
    }
}
