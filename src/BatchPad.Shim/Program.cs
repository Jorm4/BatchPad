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

var relays = Task.WhenAll(
    Relay(process.StandardOutput, Console.IsOutputRedirected, Console.OpenStandardOutput, Console.Out),
    Relay(process.StandardError, Console.IsErrorRedirected, Console.OpenStandardError, Console.Error));
await process.WaitForExitAsync();
// A process BatchPad left running may still hold the pipes open.
await Task.WhenAny(relays, Task.Delay(TimeSpan.FromSeconds(1)));
return process.ExitCode;

// A pipe or file gets BatchPad's UTF-8 bytes unchanged; a console needs text in its own code page.
static async Task Relay(StreamReader from, bool redirected, Func<Stream> openRaw, TextWriter console)
{
    if (redirected)
    {
        await using var raw = openRaw();
        await from.BaseStream.CopyToAsync(raw);
        return;
    }
    var buffer = new char[4096];
    int read;
    while ((read = await from.ReadAsync(buffer)) > 0)
    {
        console.Write(buffer, 0, read);
        console.Flush();
    }
}
