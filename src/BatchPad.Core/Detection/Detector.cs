using System.Text.RegularExpressions;

namespace BatchPad.Core.Detection;

public static partial class Detector
{
    /// <param name="probes">Runs the Python and PowerShell parameter probes; without it those scripts get no parameters.</param>
    /// <param name="cachedProbesOnly">Uses only probe results already cached, starting no process.</param>
    public static DetectionResult Detect(string path, ScriptProbes? probes = null, bool cachedProbesOnly = false)
    {
        var result = Detect(Path.GetFileName(path), File.ReadAllText(path));
        if (probes?.Parameters(path, cachedProbesOnly) is { } probed)
            result.Parameters = probed;
        return result;
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
        if (Path.GetFileNameWithoutExtension(fileName).Contains("serve", StringComparison.OrdinalIgnoreCase))
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
