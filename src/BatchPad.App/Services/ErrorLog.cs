namespace BatchPad.App.Services;

/// <summary>Unexpected errors, appended to <c>errors.log</c> in the local data folder; the oldest half goes past 1 MB.</summary>
public sealed class ErrorLog(string path)
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _lock = new();

    public string Path { get; } = path;

    public void Append(Exception? exception)
    {
        if (exception is null)
            return;
        lock (_lock)
        {
            IoProblems.TryIo(() =>
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                if (new FileInfo(Path) is { Exists: true, Length: > MaxBytes } file)
                {
                    var text = File.ReadAllText(file.FullName);
                    File.WriteAllText(file.FullName, text[(text.Length / 2)..]);
                }
                File.AppendAllText(Path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            });
        }
    }
}
