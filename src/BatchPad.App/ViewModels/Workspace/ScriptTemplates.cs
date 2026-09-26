using BatchPad.Core.Detection;

namespace BatchPad.App.ViewModels.Workspace;

public sealed record ScriptTemplate(string Label, string Extension)
{
    public override string ToString() => Label;
}

/// <summary>The starting content "New script…" writes (§5.1), embedded from <c>Templates/new.*</c>.</summary>
public static class ScriptTemplates
{
    public static IReadOnlyList<ScriptTemplate> All { get; } =
    [
        new("Batch (.bat)", ".bat"),
        new("Python (.py)", ".py"),
        new("PowerShell (.ps1)", ".ps1"),
        new("C# (.cs)", ".cs"),
    ];

    public static string Content(string extension, string fileName)
    {
        using var stream = typeof(ScriptTemplates).Assembly.GetManifestResourceStream($"BatchPad.App.Templates.new{extension}")
            ?? throw new ArgumentException($"No template for {extension}.", nameof(extension));
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("{name}", Detector.ReadableName(fileName)).Replace("{file}", fileName);
    }
}
