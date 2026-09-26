using System.Globalization;
using BatchPad.Core.Model;

namespace BatchPad.Core.Running;

/// <summary>Lock keys (§4 "Locks and concurrency"): <c>&lt;checkout directory&gt;|&lt;name&gt;</c>, or the bare name machine-wide.</summary>
public static class LockKeys
{
    private const char Separator = '|';

    public static string For(string name, LockScope? scope, string checkoutDirectory) =>
        scope == LockScope.Machine ? name : checkoutDirectory + Separator + name;

    public static string NameOf(string key)
    {
        var separator = key.IndexOf(Separator);
        return separator > 0 && Path.IsPathRooted(key[..separator]) ? key[(separator + 1)..] : key;
    }

    internal static string DescribeHolder(string holder, string? holderCheckout, string? waiterCheckout, DateTimeOffset since)
    {
        var elsewhere = holderCheckout is not null && !string.Equals(holderCheckout, waiterCheckout, StringComparison.OrdinalIgnoreCase)
            ? $" in {Path.GetFileName(holderCheckout)}"
            : "";
        return $"held by {holder}{elsewhere} since {since.ToString("HH:mm", CultureInfo.InvariantCulture)}";
    }
}
