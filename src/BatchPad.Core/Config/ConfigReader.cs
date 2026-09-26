using System.Text.Json;
using BatchPad.Core.Model;

namespace BatchPad.Core.Config;

public sealed class ConfigException(string path, string message, Exception? inner = null)
    : Exception($"{path}: {message}", inner)
{
    public string FilePath { get; } = path;
}

public sealed class FileTooLargeException(string path)
    : IOException($"{path} is larger than {ConfigReader.MaxFileBytes / (1024 * 1024)} MB, so BatchPad does not read it.");

public static class ConfigReader
{
    public const long MaxFileBytes = 8 * 1024 * 1024;

    public static WorkspaceFile ReadFile(string path) => Parse(ReadText(path), path);

    public static string ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxFileBytes)
            throw new FileTooLargeException(path);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

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
