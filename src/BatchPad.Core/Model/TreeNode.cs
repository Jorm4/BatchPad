using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchPad.Core.Model;

public abstract class TreeNode
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed class FolderNode : TreeNode
{
    public string? Folder { get; set; }
    public bool Collapsed { get; set; }
    public List<TreeNode> Items { get; set; } = [];
}

public sealed class LinkNode : TreeNode
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Url { get; set; }
    public string? Icon { get; set; }
    public List<string>? Tags { get; set; }
}

/// <summary>Fields shared by scripts and workflows. Null means "not set", so a customisation overlays only what it sets.</summary>
public abstract class RunnableNode : TreeNode
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public List<ParameterDefinition>? Params { get; set; }
    public Dictionary<string, string>? Env { get; set; }
    public string? Lock { get; set; }
    public string? Confirm { get; set; }
    public string? NameTemplate { get; set; }
    public bool? Hidden { get; set; }
    public List<string>? Tags { get; set; }
    public string? Icon { get; set; }
    public string? Hotkey { get; set; }
}
