using System.ComponentModel;
using System.Diagnostics;

namespace BatchPad.Core.Running;

internal sealed record CapturedOutput(int ExitCode, IReadOnlyList<string> Lines, IReadOnlyList<string> Errors)
{
    public string? FirstError => Errors.Select(e => e.Trim()).FirstOrDefault(e => e.Length > 0);
}

/// <summary>Runs a short helper process with no window, captures its output and kills its tree when it overruns.</summary>
internal static class CapturedProcess
{
    /// <exception cref="TimeoutException">It did not finish within <paramref name="timeout"/>.</exception>
    /// <exception cref="RunException">It could not be started.</exception>
    public static CapturedOutput Run(CommandLine line, TimeSpan timeout, string? input = null)
    {
        var start = line.ToStartInfo();
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.CreateNoWindow = true;
        var elapsed = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new RunException($"'{line.FileName}' could not be started.");
        }
        catch (Win32Exception ex)
        {
            throw new RunException($"'{line.FileName}' could not be started: {ex.Message}");
        }

        using (process)
        {
            var lines = new List<string>();
            var errors = new List<string>();
            var reading = Task.Run(() => Collect(process.StandardOutput.BaseStream, lines));
            var readingErrors = Task.Run(() => Collect(process.StandardError.BaseStream, errors));
            try
            {
                if (input is not null)
                    process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            if (!process.WaitForExit(Remaining(timeout, elapsed)))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
                throw new TimeoutException($"'{line.FileName}' did not finish within {timeout.TotalSeconds:0.#} s.");
            }
            Task.WaitAll([reading, readingErrors], Remaining(timeout, elapsed));
            return new CapturedOutput(process.ExitCode, Snapshot(lines), Snapshot(errors));
        }
    }

    private static void Collect(Stream stream, List<string> into)
    {
        try
        {
            LineDecoder.ReadLines(stream, line =>
            {
                lock (into)
                    into.Add(line);
            });
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch elapsed) =>
        timeout > elapsed.Elapsed ? timeout - elapsed.Elapsed : TimeSpan.Zero;

    private static List<string> Snapshot(List<string> lines)
    {
        lock (lines)
            return [.. lines];
    }
}
