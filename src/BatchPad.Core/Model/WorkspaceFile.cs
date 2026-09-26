using System.Text.Json.Serialization;

namespace BatchPad.Core.Model;

/// <summary>Shape shared by batchpad.json, global.json and user.json (§3.5–3.7).</summary>
public sealed class WorkspaceFile : ExtensibleObject
{
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public Dictionary<string, string>? Variables { get; set; }
    public ScriptNode? Defaults { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? EnvFile { get; set; }
    public List<string>? Include { get; set; }
    public List<string>? Libraries { get; set; }
    public Dictionary<string, List<string>>? Lists { get; set; }
    public Dictionary<string, ParameterDefinition>? SharedParams { get; set; }
    public List<ScriptFolder>? ScriptFolders { get; set; }
    public List<TreeNode> Scripts { get; set; } = [];

    /// <summary>user.json only: discovered script paths already opened, so the rest show as New (§3.10).</summary>
    public List<string>? SeenPaths { get; set; }
}

public sealed class ScriptFolder : ExtensibleObject
{
    public string Path { get; set; } = "";
    public List<string>? Include { get; set; }
    public List<string>? Exclude { get; set; }
    public bool? Recurse { get; set; }
    public bool GroupByPrefix { get; set; }
}
