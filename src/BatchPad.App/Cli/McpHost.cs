using System.Reflection;
using BatchPad.Core.Workspace;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BatchPad.App.Cli;

/// <summary>Serves <see cref="McpTools"/> over stdio (§4.5); stdout carries only protocol messages.</summary>
public static class McpHost
{
    public static async Task<int> RunAsync(AppPaths paths)
    {
        var tools = new McpTools(paths);
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
        await server.RunAsync();
        return 0;
    }
}
