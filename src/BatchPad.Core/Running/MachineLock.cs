using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BatchPad.Core.Running;

/// <summary>
/// A named lock shared by every BatchPad process of the user: an open <c>&lt;hash&gt;.lock</c> file nobody else may
/// open, next to a <c>&lt;hash&gt;.owner</c> file naming the holder. Unlike a mutex, a file handle isn't tied to the
/// thread that took it, and Windows closes it when the process dies.
/// </summary>
internal sealed class MachineLock : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LongestPoll = TimeSpan.FromMilliseconds(250);

    private readonly FileStream _file;
    private readonly string _ownerPath;
    private int _released;

    private MachineLock(FileStream file, string ownerPath)
    {
        _file = file;
        _ownerPath = ownerPath;
    }

    public static string PathFor(string directory, string name) => Path.Combine(directory, Hash(name) + ".lock");

    /// <exception cref="LockBusyException">Another process holds it and <paramref name="wait"/> is false.</exception>
    public static async Task<MachineLock?> AcquireAsync(string directory, string name, string? holder, string? checkout,
        Action<string>? waiting, bool wait, CancellationToken cancellation)
    {
        if (TryAcquire(directory, name, holder, checkout) is { } taken)
            return taken;
        if (!wait)
            throw new LockBusyException(LockKeys.NameOf(name), DescribeHolder(directory, name, checkout));
        await Task.Yield();
        string? reported = null;
        var delay = TimeSpan.FromMilliseconds(20);
        while (true)
        {
            var description = $"{LockKeys.NameOf(name)} ({DescribeHolder(directory, name, checkout)})";
            if (description != reported)
                waiting?.Invoke(reported = description);
            await Task.Delay(delay, cancellation);
            if (TryAcquire(directory, name, holder, checkout) is { } acquired)
                return acquired;
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LongestPoll.Ticks));
        }
    }

    /// <summary>"held by Build since 09:12", from the owner file; vaguer when that file is missing or unreadable.</summary>
    public static string DescribeHolder(string directory, string name, string? waiterCheckout)
    {
        try
        {
            var owner = JsonSerializer.Deserialize<Owner>(File.ReadAllText(OwnerPathFor(directory, name)), Json);
            if (owner is not null)
                return LockKeys.DescribeHolder(owner.Holder ?? $"{owner.Process} (process {owner.ProcessId})", owner.Checkout, waiterCheckout,
                    owner.Since.ToLocalTime());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return "held by another process";
    }

    public static MachineLock? TryAcquire(string directory, string name, string? holder, string? checkout)
    {
        Directory.CreateDirectory(directory);
        FileStream file;
        try
        {
            file = new FileStream(PathFor(directory, name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return null;
        }
        var taken = new MachineLock(file, OwnerPathFor(directory, name));
        taken.Hand(holder, checkout);
        return taken;
    }

    /// <summary>Names a new holder, for a lock handed on within this process.</summary>
    public void Hand(string? holder, string? checkout)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            File.WriteAllText(_ownerPath, JsonSerializer.Serialize(
                new Owner(Environment.ProcessId, process.ProcessName, holder, checkout, DateTimeOffset.Now), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
            return;
        try
        {
            File.Delete(_ownerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        _file.Dispose();
    }

    private static string OwnerPathFor(string directory, string name) => Path.Combine(directory, Hash(name) + ".owner");

    private static string Hash(string name) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant())))[..32];

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;

    private sealed record Owner(int ProcessId, string Process, string? Holder, string? Checkout, DateTimeOffset Since);
}

/// <summary>A lock was taken without waiting and someone else holds it.</summary>
public sealed class LockBusyException(string lockName, string holder)
    : Exception($"Lock '{lockName}' is {holder}.")
{
    public string LockName { get; } = lockName;
}
