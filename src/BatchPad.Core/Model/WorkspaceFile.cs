using System.Text.Json.Serialization;

namespace BatchPad.Core.Model;

/// <summary>Shape shared by batchpad.json, global.json and user.json (§3.5–3.7).</summary>
public sealed class WorkspaceFile : ExtensibleObject
{
    public const string SchemaUrl = "https://raw.githubusercontent.com/Jorm4/BatchPad/main/docs/batchpad.schema.json";

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

    /// <summary>user.json and global.json only (§4.2): the loader drops them from shared files with a load error.</summary>
    public List<Schedule>? Schedules { get; set; }

    /// <summary>user.json only: discovered script paths already opened, so the rest show as New (§3.10).</summary>
    public List<string>? SeenPaths { get; set; }

    /// <summary>user.json only: detection proposals the user dismissed, per script key (§3.10).</summary>
    public Dictionary<string, List<string>>? DismissedProposals { get; set; }
}

public sealed class ScriptFolder : ExtensibleObject
{
    public string Path { get; set; } = "";
    public List<string>? Include { get; set; }
    public List<string>? Exclude { get; set; }
    public bool? Recurse { get; set; }
    public bool GroupByPrefix { get; set; }
}
