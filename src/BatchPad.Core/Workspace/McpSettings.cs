namespace BatchPad.Core.Workspace;

public sealed class McpSettings
{
    /// <summary>The ids the MCP server's <c>run_script</c> may run; null allows every id.</summary>
    public List<string>? AllowIds { get; set; }
}
