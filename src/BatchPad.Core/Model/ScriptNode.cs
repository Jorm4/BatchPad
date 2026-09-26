using System.Text.Json.Nodes;

namespace BatchPad.Core.Model;

/// <summary>A script definition (§3.2); in user.json also a customisation of <see cref="Base"/> (§3.7).</summary>
public sealed class ScriptNode : RunnableNode
{
    public string? Base { get; set; }
    public string? Path { get; set; }
    public string? Command { get; set; }
    public string? Module { get; set; }
    public Runner? Runner { get; set; }
    public string? WorkingDir { get; set; }
    public List<string>? Args { get; set; }
    public List<string>? ArgsTemplate { get; set; }
    public string? EnvFile { get; set; }
    public ConsoleMode? Console { get; set; }
    public bool? LongRunning { get; set; }
    public ReadyDefinition? Ready { get; set; }
    public string? Stop { get; set; }
    public List<string>? ErrorPatterns { get; set; }
    public List<ArtifactDefinition>? Artifacts { get; set; }
    public List<string>? DependsOn { get; set; }
    public string? TestReport { get; set; }
    public bool? SingleInstance { get; set; }
    public string? Timeout { get; set; }
    public bool? Elevated { get; set; }
    public Dictionary<string, JsonNode?>? Values { get; set; }
    public string? ExtraArgs { get; set; }
    public Dictionary<string, Dictionary<string, JsonNode?>>? StepValues { get; set; }
}

public enum Runner { Auto, Batch, Python, Csharp, Powershell, Exe, Shell }

public enum ConsoleMode { Captured, Window, WindowKeepOpen }

public sealed class ReadyDefinition : ExtensibleObject
{
    public string? Pattern { get; set; }
    public string? Open { get; set; }
}

public sealed class ArtifactDefinition : ExtensibleObject
{
    public string? Path { get; set; }
    public ArtifactOpen? Open { get; set; }
}

public enum ArtifactOpen { Never, OnSuccess, Always }
