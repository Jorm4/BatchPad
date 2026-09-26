using System.Text;
using BatchPad.Core.Model;

namespace BatchPad.Core.Config;

public static class ConfigWriter
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public static string Serialize(WorkspaceFile file) => ConfigJson.FileText(file);

    public static void Write(string path, WorkspaceFile file) => WriteAtomic(path, Serialize(file));

    /// <summary>
    /// Re-reads the file under an exclusive lock, applies <paramref name="change"/> to that fresh copy and writes it,
    /// so another window's saves since our last read are kept. A missing file starts empty.
    /// </summary>
    public static WorkspaceFile Update(string path, Action<WorkspaceFile> change) =>
        Update(path, p => File.Exists(p) ? ConfigReader.ReadFile(p) : new WorkspaceFile(), change, Serialize);

    internal static T Update<T>(string path, Func<string, T> read, Action<T> change, Func<T, string> serialize)
    {
        using var fileLock = AcquireLock(path);
        var current = read(path);
        change(current);
        WriteAtomic(path, serialize(current));
        return current;
    }

    internal static void WriteAtomic(string path, string content, Action? beforeReplace = null)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content));
                stream.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke();
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static FileStream AcquireLock(string path)
    {
        var lockPath = Path.GetFullPath(path) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var deadline = DateTime.UtcNow + LockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }
}
