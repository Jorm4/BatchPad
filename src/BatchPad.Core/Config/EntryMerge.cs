using BatchPad.Core.Model;

namespace BatchPad.Core.Config;

/// <summary>Decides whether an open edit can be re-applied after its config file changed on disk (§3).</summary>
public static class EntryMerge
{
    /// <summary>
    /// True when the entry the edit started from reads the same in <paramref name="current"/>, so another side only touched
    /// other entries. The entry is found by id, else by path.
    /// </summary>
    /// <param name="hadEntry">False for a discovered script edited without an entry: it applies while the file still has none.</param>
    public static bool StillApplies(WorkspaceFile current, ScriptNode original, bool hadEntry)
    {
        var found = current.Scripts.Descendants().OfType<ScriptNode>().FirstOrDefault(script => original.Id is not null
            ? script.Id == original.Id
            : original.Path is not null && string.Equals(Normalized(script.Path), Normalized(original.Path), StringComparison.OrdinalIgnoreCase));
        return hadEntry ? found is not null && Json(found) == Json(original) : found is null;
    }

    private static string? Normalized(string? path) => path?.Replace('\\', '/');

    private static string Json(ScriptNode node) => ConfigJson.Serialize(node);
}
