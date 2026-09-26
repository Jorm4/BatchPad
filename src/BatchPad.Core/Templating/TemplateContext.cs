namespace BatchPad.Core.Templating;

/// <summary>What <c>${…}</c> variables resolve against (§3.4). A null or missing entry makes that variable unknown.</summary>
public sealed record TemplateContext
{
    public string? WorkspaceDir { get; init; }
    public string? ScriptDir { get; init; }
    public IReadOnlyDictionary<string, string>? Variables { get; init; }
    public IReadOnlyDictionary<string, TemplateValue> Params { get; init; } = new Dictionary<string, TemplateValue>();
    public TemplateValue? Item { get; init; }
    public IReadOnlyDictionary<string, string>? Workflow { get; init; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? Steps { get; init; }
    public Func<string, string?> Environment { get; init; } = System.Environment.GetEnvironmentVariable;
}
