using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BatchPad.Core.Config;

namespace BatchPad.Core.Model;

public sealed class WorkflowNode : RunnableNode
{
    public List<WorkflowStep> Steps { get; set; } = [];
}

public sealed class WorkflowStep : ExtensibleObject
{
    public string? Id { get; set; }
    public string? Run { get; set; }
    public Dictionary<string, JsonNode?>? Values { get; set; }
    public StepWhen? When { get; set; }
    public string? ForEach { get; set; }
    public List<string>? EmptyArgs { get; set; }
    public bool? Confirm { get; set; }
    public bool? ContinueOnError { get; set; }
    public bool? FailFast { get; set; }
    public StepRetry? Retry { get; set; }

    /// <summary>The members of a <c>parallel</c> group; this step then runs nothing itself.</summary>
    [JsonIgnore]
    public List<WorkflowStep>? Parallel { get; set; }

    /// <summary>How many <c>forEach</c> items run at once (<c>"parallel": n</c>).</summary>
    [JsonIgnore]
    public int? MaxParallel { get; set; }

    [JsonInclude, JsonPropertyName("parallel"), JsonConverter(typeof(StepParallelConverter))]
    internal StepParallel? ParallelJson
    {
        get => Parallel is not null ? new StepParallel(Parallel, null) : MaxParallel is { } degree ? new StepParallel(null, degree) : null;
        set => (Parallel, MaxParallel) = (value?.Members, value?.Degree);
    }

    [JsonIgnore]
    public bool IsGroup => Parallel is not null;

    /// <summary>This step, or every member of it when it is a group, recursively.</summary>
    public IEnumerable<WorkflowStep> Leaves() => Parallel is { } members ? members.SelectMany(m => m.Leaves()) : [this];
}

public sealed class StepRetry : ExtensibleObject
{
    public int Count { get; set; }
    public double DelaySeconds { get; set; }
}

internal sealed record StepParallel(List<WorkflowStep>? Members, int? Degree);

public enum StepWhen { Success, Failure, Always }
