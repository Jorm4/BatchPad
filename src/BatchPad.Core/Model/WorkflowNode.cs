using System.Text.Json.Nodes;

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
}

public enum StepWhen { Success, Failure, Always }
