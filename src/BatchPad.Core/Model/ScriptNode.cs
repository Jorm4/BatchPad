using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BatchPad.Core.Config;

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
    [JsonConverter(typeof(PathOrObjectConverter<TestReportDefinition>))]
    public TestReportDefinition? TestReport { get; set; }
    [JsonConverter(typeof(PathOrObjectConverter<BenchmarkReportDefinition>))]
    public BenchmarkReportDefinition? BenchmarkReport { get; set; }
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

/// <summary><c>testReport</c>: a JUnit XML path, or an object that also names the parameter "Re-run failed" fills (§5).</summary>
public sealed class TestReportDefinition : ExtensibleObject, IReportDefinition
{
    public string? Path { get; set; }
    public string? RerunParam { get; set; }
    public RerunBy? RerunBy { get; set; }

    [JsonIgnore]
    public bool IsPlain => RerunParam is null && RerunBy is null && ExtensionData is not { Count: > 0 };
}

public enum RerunBy { Case, Suite }

/// <summary>A report file a script writes: a path string, or an object when it has more settings than the path.</summary>
public interface IReportDefinition
{
    string? Path { get; set; }
    bool IsPlain { get; }
}

/// <summary><c>benchmarkReport</c>: a Google Benchmark JSON path, or an object that also sets the regression threshold in percent.</summary>
public sealed class BenchmarkReportDefinition : ExtensibleObject, IReportDefinition
{
    public const string GoogleFormat = "google";
    public const double DefaultThreshold = 5;

    public string? Path { get; set; }
    public string? Format { get; set; }
    public double? Threshold { get; set; }

    [JsonIgnore]
    public bool IsPlain => Format is null && Threshold is null && ExtensionData is not { Count: > 0 };
}
