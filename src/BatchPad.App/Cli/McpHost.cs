using System.Reflection;
using BatchPad.Core.Workspace;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BatchPad.App.Cli;

/// <summary>Serves <see cref="McpTools"/> over stdio (§4.5); stdout carries only protocol messages.</summary>
public static class McpHost
{
    public static async Task<int> RunAsync(AppPaths paths, TextWriter error)
    {
        if (App.LoadSettings(paths, error).Mcp?.Enabled != true)
        {
            error.WriteLine(McpSettings.TurnedOff);
            return CliRunner.Failure;
        }
        var tools = new McpTools(paths);
        tools.StartTelemetry();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "batchpad",
                Version = typeof(McpHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0",
            },
            ToolCollection = [],
        };
        foreach (var method in typeof(McpTools).GetMethods().Where(m => m.IsDefined(typeof(McpServerToolAttribute))))
            options.ToolCollection.Add(McpServerTool.Create(method, tools));

        await using var transport = new StdioServerTransport(options);
        await using var server = McpServer.Create(transport, options);
        var running = server.RunAsync();
        // The server waits for calls in flight before it returns, so stop their runs as soon as stdin closes.
        await Task.WhenAny(running, transport.MessageReader.Completion);
        await tools.DisposeAsync();
        await running;
        return 0;
    }
}
