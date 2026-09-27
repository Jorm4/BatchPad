using BatchPad.Core.Model;
using BatchPad.Core.Workspace;

namespace BatchPad.Core.Detection;

/// <summary>What detection suggests for a script that its entry doesn't have yet and the user hasn't dismissed (§3.10).</summary>
public sealed record ScriptProposals(IReadOnlyList<ParameterDefinition> Parameters, string? LongRunningReason, string? StopCompanion)
{
    public static readonly ScriptProposals None = new([], null, null);

    public bool IsEmpty => Parameters.Count == 0 && LongRunningReason is null && StopCompanion is null;

    /// <summary>The tree badge text, e.g. <c>1 new option: --gated</c>, naming at most three; null when there is nothing to propose.</summary>
    public string? Badge
    {
        get
        {
            var parts = new List<string>();
            if (Parameters.Count > 0)
                parts.Add($"{Parameters.Count} new option{(Parameters.Count == 1 ? "" : "s")}"
                    + (Parameters.Count <= 3 ? ": " + string.Join(", ", Parameters.Select(p => p.Arg ?? p.Name)) : ""));
            if (LongRunningReason is not null)
                parts.Add("long-running");
            if (StopCompanion is not null)
                parts.Add($"stop with {Path.GetFileName(StopCompanion)}");
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }
    }
}

public static class ProposalTracker
{
    public const string LongRunningKey = "longRunning";

    public static string KeyOf(ParameterDefinition parameter) => "param:" + (parameter.Arg ?? parameter.Name);

    public static string StopKey(string companionPath) => "stop:" + companionPath.Replace('\\', '/');

    /// <summary>The key a script's dismissals are stored under in <c>user.json</c>: its tree and its path relative to that tree's folder.</summary>
    public static string ScriptKey(TreeKind tree, string relativePath) => $"{tree.ToString().ToLowerInvariant()}:{relativePath.Replace('\\', '/')}";

    /// <param name="scriptPath">The script file, absolute.</param>
    public static ScriptProposals For(ScriptNode entry, string scriptPath, IReadOnlyCollection<string> dismissed,
        ScriptProbes? probes = null, bool cachedProbesOnly = false)
    {
        if (!File.Exists(scriptPath))
            return ScriptProposals.None;
        var detected = Detector.Detect(scriptPath, probes, cachedProbesOnly);
        var taken = (entry.Params ?? []).SelectMany(p => new[] { p.Name, p.Use, p.Arg }).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameters = detected.Parameters
            .Where(p => !taken.Contains(p.Name ?? "") && !(p.Arg is { } arg && taken.Contains(arg)) && !dismissed.Contains(KeyOf(p)))
            .ToList();
        var longRunning = entry.LongRunning is null && !dismissed.Contains(LongRunningKey) ? detected.LongRunningReason : null;
        var stop = entry.Stop is null && StopCompanionFor(scriptPath, detected, probes, cachedProbesOnly) is { } companion && !dismissed.Contains(StopKey(companion))
            ? companion
            : null;
        return new ScriptProposals(parameters, longRunning, stop);
    }

    /// <summary>For <c>serve_X</c>, a <c>stop_X</c> beside it that takes the same parameters (or neither takes any); relative to the script's folder.</summary>
    public static string? StopCompanionFor(string scriptPath, DetectionResult? detected = null, ScriptProbes? probes = null,
        bool cachedProbesOnly = false)
    {
        var name = Path.GetFileNameWithoutExtension(scriptPath);
        if (!name.StartsWith("serve_", StringComparison.OrdinalIgnoreCase))
            return null;
        var folder = Path.GetDirectoryName(scriptPath)!;
        var stem = "stop_" + name["serve_".Length..];
        var companion = Directory.EnumerateFiles(folder, stem + ".*")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(stem, StringComparison.OrdinalIgnoreCase));
        if (companion is null)
            return null;
        var serveNames = (detected ?? Detector.Detect(scriptPath, probes, cachedProbesOnly)).Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stopNames = Detector.Detect(companion, probes, cachedProbesOnly).Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return serveNames.Count == stopNames.Count && serveNames.Count == 0 || serveNames.Overlaps(stopNames) ? Path.GetFileName(companion) : null;
    }
}
