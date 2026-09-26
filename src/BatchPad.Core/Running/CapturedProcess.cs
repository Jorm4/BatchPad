using System.ComponentModel;
using System.Diagnostics;

namespace BatchPad.Core.Running;

public sealed record CapturedOutput(int ExitCode, IReadOnlyList<string> Lines, IReadOnlyList<string> Errors)
{
    public string? FirstError => Errors.Select(e => e.Trim()).FirstOrDefault(e => e.Length > 0);
}

/// <summary>Runs a short helper process with no window, captures its output and kills its tree when it overruns.</summary>
public static class CapturedProcess
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
            var reading = Task.Run(() => LineDecoder.ReadLines(process.StandardOutput.BaseStream, lines.Add));
            var readingErrors = Task.Run(() => LineDecoder.ReadLines(process.StandardError.BaseStream, errors.Add));
            try
            {
                if (input is not null)
                    process.StandardInput.Write(input);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
            }
            if (!process.WaitForExit(timeout))
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
            Task.WaitAll([reading, readingErrors], timeout);
            return new CapturedOutput(process.ExitCode, lines, errors);
        }
    }
}
