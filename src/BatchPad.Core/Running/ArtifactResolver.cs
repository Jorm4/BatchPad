using BatchPad.Core.Model;
using BatchPad.Core.Templating;
using BatchPad.Core.Trust;

namespace BatchPad.Core.Running;

public sealed record ResolvedArtifact(string Path, ArtifactOpen Open)
{
    public bool Exists => File.Exists(Path) || Directory.Exists(Path);

    public bool OpensAfter(RunResult result) => Open switch
    {
        ArtifactOpen.Always => true,
        ArtifactOpen.OnSuccess => result.Succeeded,
        _ => false,
    };
}

/// <summary>Expands a script's <c>artifacts</c> into full paths and opens them per their <c>open</c> policy (§3.2).</summary>
public static class ArtifactResolver
{
    public static IReadOnlyList<ResolvedArtifact> Resolve(RunRequest request) =>
        Resolve(request.Script, request.Tree.BaseDirectory, RunPlanner.BoundTemplatesFor(request));

    public static IReadOnlyList<ResolvedArtifact> Resolve(ScriptNode script, string baseDirectory, TemplateContext templates) =>
        (script.Artifacts ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a.Path))
            .Select(a => new ResolvedArtifact(
                TemplateExpander.ExpandPath(a.Path!, baseDirectory, templates),
                a.Open ?? ArtifactOpen.Never))
            .ToList();

    /// <returns>The artifacts opened: those whose policy allows it after <paramref name="result"/>, that exist and are not programs.</returns>
    public static IReadOnlyList<ResolvedArtifact> OpenAfter(RunResult result, IEnumerable<ResolvedArtifact> artifacts, IShellOpener opener)
    {
        var opened = artifacts.Where(a => a.OpensAfter(result) && LinkPolicy.OpensUnasked(a.Path) && a.Exists).ToList();
        foreach (var artifact in opened)
            opener.Open(artifact.Path);
        return opened;
    }
}
