using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BatchPad.Core.Config;

namespace BatchPad.Core.Model;

public enum MissedPolicy { Skip, RunOnce }

public enum OverlapPolicy { Skip, Queue, Parallel }

public enum RunIn { App, Windows }

/// <summary>A trigger attached to a script or workflow, with fixed values (§4.2). Only user.json and global.json hold them.</summary>
public sealed class Schedule : ExtensibleObject
{
    public string? Id { get; set; }
    public string Target { get; set; } = "";

    /// <summary>global.json: the workspace file or folder a <c>workspace:</c> target belongs to.</summary>
    public string? Workspace { get; set; }

    public Dictionary<string, JsonNode?>? Values { get; set; }
    public Trigger Trigger { get; set; } = new();
    public MissedPolicy Missed { get; set; }
    public OverlapPolicy Overlap { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Windows: a Task Scheduler task runs it, so it runs while BatchPad is closed too.</summary>
    public RunIn RunIn { get; set; }

    /// <summary>The target's definition when the user last confirmed it; a different one pauses the schedule.</summary>
    public string? DefinitionHash { get; set; }

    /// <summary>The id, else a hash of the target and trigger, so an unnamed schedule keeps its state across loads.</summary>
    [JsonIgnore]
    public string Key => Id is { Length: > 0 } id ? id
        : "h" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Target + "\n" + JsonSerializer.Serialize(Trigger, ConfigJson.Options))))[..12];
}
