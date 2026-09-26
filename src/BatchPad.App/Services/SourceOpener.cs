using BatchPad.Core.Output;
using BatchPad.Core.Workspace;

namespace BatchPad.App.Services;

/// <summary>Opens an output line's source reference in the configured editor, VS Code when on PATH, else the file's default app.</summary>
public sealed class SourceOpener(IShellService shell, Settings settings, Func<bool>? codeOnPath = null)
{
    private readonly Lazy<bool> _codeOnPath = new(codeOnPath ?? (() => EditorCommand.IsOnPath("code")));

    public void Open(SourceLocation location)
    {
        if (EditorCommand.Template(settings.EditorCommand, _codeOnPath.Value, location.Path) is { } template)
            shell.RunCommand(EditorCommand.Expand(template, location), EditorCommand.Environment(location));
        else
            shell.Open(location.Path);
    }
}
