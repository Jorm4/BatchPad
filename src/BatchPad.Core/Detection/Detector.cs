using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using BatchPad.Core.Config;

namespace BatchPad.Core.Detection;

public static partial class Detector
{
    private static readonly ConcurrentDictionary<string, (DateTime Stamp, long Length, DetectionResult Result)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <param name="probes">Runs the Python and PowerShell parameter probes; without it those scripts get no parameters.</param>
    /// <param name="cachedProbesOnly">Uses only probe results already cached, starting no process.</param>
    public static DetectionResult Detect(string path, ScriptProbes? probes = null, bool cachedProbesOnly = false)
    {
        var file = new FileInfo(path);
        var (stamp, length) = (file.LastWriteTimeUtc, file.Length);
        if (!Cache.TryGetValue(file.FullName, out var cached) || cached.Stamp != stamp || cached.Length != length)
        {
            if (Cache.Count > 2000)
                Cache.Clear();
            Cache[file.FullName] = cached = (stamp, length, Detect(file.Name, ReadOrEmpty(path)));
        }
        return new DetectionResult
        {
            Name = cached.Result.Name,
            Description = cached.Result.Description,
            LongRunningReason = cached.Result.LongRunningReason,
            Parameters = probes?.Parameters(path, cachedProbesOnly) ?? ConfigJson.Clone(cached.Result.Parameters),
        };
    }

    private static string ReadOrEmpty(string path)
    {
        try
        {
            return ConfigReader.ReadText(path);
        }
        catch (FileTooLargeException)
        {
            return "";
        }
    }

    public static DetectionResult Detect(string fileName, string content)
    {
        var lines = content.ReplaceLineEndings("\n").Split('\n');
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return new DetectionResult
        {
            Name = ReadableName(fileName),
            Description = CommentHeader.Description(CommentHeader.Read(extension, lines)),
            Parameters = extension is ".bat" or ".cmd" ? BatchParameterDetector.Detect(fileName, lines) : [],
            LongRunningReason = LongRunningReason(fileName, content),
        };
    }

    /// <summary><c>package_web.bat</c> → <c>Package web</c>.</summary>
    public static string ReadableName(string fileName)
    {
        var words = WordSeparators().Replace(Path.GetFileNameWithoutExtension(fileName), " ").Trim();
        return words.Length == 0 ? fileName : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static string? LongRunningReason(string fileName, string content)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (WordSeparators().Split(stem)[0].ToLowerInvariant() is "stop" or "kill" or "shutdown")
            return null;
        if (stem.Contains("serve", StringComparison.OrdinalIgnoreCase))
            return "\"serve\" in the name";
        if (content.Contains("http.server", StringComparison.Ordinal))
            return "starts http.server";
        if (content.Contains("Press Ctrl+C", StringComparison.OrdinalIgnoreCase))
            return "prints \"Press Ctrl+C\"";
        return null;
    }

    [GeneratedRegex(@"[_\-\s]+")]
    private static partial Regex WordSeparators();
}
