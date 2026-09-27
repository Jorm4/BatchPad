using BatchPad.Core.Workspace;

namespace BatchPad.Core.Running;

/// <summary>Saved secret values for runs nobody can answer prompts for, keyed by target name, each with the folder it was saved for.</summary>
public interface ISecretStore
{
    public static readonly ISecretStore None = new NoSecretStore();

    (string Value, string? Root)? Get(string target);

    void Set(string target, string value, string? root);

    /// <returns>Whether there was an entry to remove.</returns>
    bool Remove(string target);

    /// <summary>The target names starting with <paramref name="prefix"/> and their roots, never their values.</summary>
    IReadOnlyList<(string Target, string? Root)> List(string prefix);
}

internal sealed class NoSecretStore : ISecretStore
{
    public (string Value, string? Root)? Get(string target) => null;

    public void Set(string target, string value, string? root) => throw new InvalidOperationException("There is no secret store to save to.");

    public bool Remove(string target) => false;

    public IReadOnlyList<(string Target, string? Root)> List(string prefix) => [];
}

/// <summary>
/// Where a tree's secrets are saved: <c>BatchPad:global:&lt;parameter&gt;</c> for Global scripts, else
/// <c>BatchPad:ws:&lt;workspace id&gt;:&lt;parameter&gt;</c>, readable only from the folder it was saved for.
/// </summary>
/// <param name="Root">Null for Global secrets, which any workspace reads.</param>
public sealed record SecretScope(string Prefix, string? Root)
{
    public static readonly SecretScope Global = new("BatchPad:global:", null);

    public static SecretScope Of(LoadedWorkspace workspace, TreeKind tree) => tree == TreeKind.Global ? Global : Of(workspace);

    /// <summary>
    /// The workspace's id comes from its batchpad.json, which anyone can copy, so its secrets are also bound to its folder:
    /// the repository's main checkout, shared by its worktrees, or else the workspace folder.
    /// </summary>
    public static SecretScope Of(LoadedWorkspace workspace) =>
        new($"BatchPad:ws:{workspace.Id}:", PathIdentity.Normalize(Checkout.Locate(workspace.Directory)?.Repository ?? workspace.Directory));

    public string Target(string parameter) => Prefix + parameter;

    public string? Get(ISecretStore store, string parameter) => store.Get(Target(parameter)) is { } saved && Owns(saved.Root) ? saved.Value : null;

    public void Set(ISecretStore store, string parameter, string value) => store.Set(Target(parameter), value, Root);

    public bool Remove(ISecretStore store, string parameter) =>
        store.Get(Target(parameter)) is { } saved && Owns(saved.Root) && store.Remove(Target(parameter));

    public IReadOnlyList<string> Names(ISecretStore store) =>
        [.. store.List(Prefix).Where(e => Owns(e.Root)).Select(e => e.Target[Prefix.Length..]).Order(StringComparer.OrdinalIgnoreCase)];

    private bool Owns(string? root) => Root is null || string.Equals(root, Root, StringComparison.OrdinalIgnoreCase);
}
