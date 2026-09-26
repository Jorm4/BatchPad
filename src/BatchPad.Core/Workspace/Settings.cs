using System.Text.Json;
using BatchPad.Core.Config;
using BatchPad.Core.Model;

namespace BatchPad.Core.Workspace;

public sealed class Settings : ExtensibleObject
{
    public const int MaxRecentWorkspaces = 10;

    public List<string> RecentWorkspaces { get; set; } = [];
    public Dictionary<string, string> Interpreters { get; set; } = [];
    public List<string> TrustedFolders { get; set; } = [];
    public WindowLayout? Window { get; set; }
    public string? EditorCommand { get; set; }
    public bool KeepRunningInTray { get; set; } = true;

    public static Settings Load(string path)
    {
        if (!File.Exists(path))
            return new Settings();
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), ConfigJson.Options) ?? new Settings();
        }
        catch (JsonException ex)
        {
            throw new ConfigException(path, ex.Message, ex);
        }
    }

    public void Save(string path) =>
        ConfigWriter.WriteAtomic(path, JsonSerializer.Serialize(this, ConfigJson.Options) + "\n");

    public void AddRecentWorkspace(string workspaceFile)
    {
        var fullPath = Path.GetFullPath(workspaceFile);
        RecentWorkspaces.RemoveAll(p => string.Equals(p, fullPath, StringComparison.OrdinalIgnoreCase));
        RecentWorkspaces.Insert(0, fullPath);
        if (RecentWorkspaces.Count > MaxRecentWorkspaces)
            RecentWorkspaces.RemoveRange(MaxRecentWorkspaces, RecentWorkspaces.Count - MaxRecentWorkspaces);
    }
}

public sealed class WindowLayout : ExtensibleObject
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    public double TreeWidth { get; set; }
    public double OutputHeight { get; set; }
}
