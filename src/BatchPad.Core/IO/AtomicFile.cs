namespace BatchPad.Core.IO;

/// <summary>Writes a file so readers see the old content or the new, never a partial one.</summary>
public static class AtomicFile
{
    public const string TemporaryExtension = ".tmp";

    /// <param name="overwrite">False to fail with an <see cref="IOException"/> when <paramref name="path"/> already exists.</param>
    public static void WriteAllText(string path, string contents, bool overwrite = true)
    {
        var temporary = $"{path}.{Environment.ProcessId:x}{Path.GetRandomFileName()}{TemporaryExtension}";
        try
        {
            File.WriteAllText(temporary, contents);
            File.Move(temporary, path, overwrite);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
