using BatchPad.App.Cli;

namespace BatchPad.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--version"])
        {
            App.PrintVersion();
            return 0;
        }
        if (CliCommand.IsCli(args))
            return App.RunCli(args);
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
