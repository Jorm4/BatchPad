using System.Text.Json;
using BatchPad.Core.Model;

namespace BatchPad.Core.Config;

public sealed class ConfigException(string path, string message, Exception? inner = null)
    : Exception($"{path}: {message}", inner)
{
    public string FilePath { get; } = path;
}

public static class ConfigReader
{
    public static WorkspaceFile ReadFile(string path) => Parse(File.ReadAllText(path), path);

    public static WorkspaceFile Parse(string json, string sourceName = "<string>")
    {
        try
        {
            return JsonSerializer.Deserialize<WorkspaceFile>(json, ConfigJson.Options)
                ?? throw new ConfigException(sourceName, "the file contains null instead of an object.");
        }
        catch (JsonException ex)
        {
            throw new ConfigException(sourceName, ex.Message, ex);
        }
    }
}
