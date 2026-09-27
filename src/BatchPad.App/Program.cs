using BatchPad.App.Cli;

namespace BatchPad.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--data-dir", var dataDirectory, .. var rest])
        {
            Environment.SetEnvironmentVariable(App.DataDirectoryVariable, dataDirectory);
            args = rest;
        }
        if (args is ["--version"])
        {
            App.PrintVersion();
            return 0;
        }
        if (CliCommand.IsCli(args))
            return App.RunCli(args);
        if (GuiArguments.Parse(args) is { RunKey: { } runKey } arguments && App.HandOff(arguments.Workspace, runKey))
            return 0;
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

public sealed record GuiArguments(string? Workspace, string? RunKey)
{
    public static GuiArguments Parse(IReadOnlyList<string> args)
    {
        string? workspace = null, runKey = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--run" when i + 1 < args.Count:
                    runKey = args[++i];
                    break;
                case "--workspace" or "-w" when i + 1 < args.Count:
                    workspace = args[++i];
                    break;
                case var path when !path.StartsWith("--", StringComparison.Ordinal):
                    workspace ??= path;
                    break;
            }
        }
        return new GuiArguments(workspace, runKey);
    }
}
