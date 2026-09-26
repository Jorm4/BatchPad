using BatchPad.Core.Model;

namespace BatchPad.Core.Detection;

/// <summary>Settings proposed by reading a script without running it (§3.10). Nothing here applies until accepted.</summary>
public sealed class DetectionResult
{
    public required string Name { get; init; }
    public string? Description { get; init; }
    public List<ParameterDefinition> Parameters { get; set; } = [];
    public string? LongRunningReason { get; init; }
    public bool LongRunning => LongRunningReason is not null;
}
