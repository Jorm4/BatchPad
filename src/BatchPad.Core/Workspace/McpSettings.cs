namespace BatchPad.Core.Workspace;

public sealed class McpSettings
{
    public const string TurnedOff = "The BatchPad MCP server is off. Turn on \"Let coding agents run scripts\" in BatchPad's Settings.";

    /// <summary>Off unless the user turns it on; lives only in the user's settings, never in a workspace.</summary>
    public bool Enabled { get; set; }

    /// <summary>The ids the MCP server's <c>run_script</c> may run; null allows every id.</summary>
    public List<string>? AllowIds { get; set; }
}
