using System.Collections.Concurrent;
using System.ComponentModel;
using BatchPad.Core.Running;

namespace BatchPad.Tests;

internal sealed class FakeSecretStore : ISecretStore
{
    public ConcurrentDictionary<string, (string Value, string? Root)> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Win32Exception? Failure { get; set; }

    public (string Value, string? Root)? Get(string target) =>
        Failure is { } failure ? throw failure : Entries.TryGetValue(target, out var entry) ? entry : null;

    public void Set(string target, string value, string? root) => Entries[target] = Failure is { } failure ? throw failure : (value, root);

    public bool Remove(string target) => Failure is { } failure ? throw failure : Entries.TryRemove(target, out _);

    public IReadOnlyList<(string Target, string? Root)> List(string prefix) => Failure is { } failure
        ? throw failure
        : [.. Entries.Where(e => e.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(e => (e.Key, e.Value.Root))];
}
