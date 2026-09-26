using System.ComponentModel;
using System.Text;
using BatchPad.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace BatchPad.Core.Running;

/// <summary>One process to start: its exact command line and its complete environment.</summary>
public sealed record RunSpec(CommandLine Command, IReadOnlyDictionary<string, string> Environment)
{
    public ConsoleMode Console { get; init; } = ConsoleMode.Captured;
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Starts runs inside Job Objects (§4).</summary>
internal static class ProcessRunner
{
    // Serialises inheritable-handle creation, so a concurrent launch cannot inherit another run's pipe ends.
    private static readonly Lock CreateProcessLock = new();

    public static RunHandle Start(RunSpec spec, TimeProvider? time = null) => Start([spec], time);

    /// <summary>Runs <paramref name="specs"/> one after another, stopping at the first failure.</summary>
    /// <exception cref="RunException">The first process could not be started.</exception>
    public static RunHandle Start(IReadOnlyList<RunSpec> specs, TimeProvider? time = null)
    {
        var handle = Create(specs, time);
        handle.Begin();
        return handle;
    }

    /// <summary>A run that starts when <see cref="RunHandle.Begin"/> is called, e.g. once its locks are free.</summary>
    internal static RunHandle Create(IReadOnlyList<RunSpec> specs, TimeProvider? time = null) =>
        specs.Count == 0
            ? throw new ArgumentException("Nothing to run.", nameof(specs))
            : new RunHandle(specs, time ?? TimeProvider.System);

    internal static unsafe StartedProcess Launch(RunSpec spec, Action<string, OutputStream> onLine)
    {
        var captured = spec.Console == ConsoleMode.Captured;
        var job = new JobObject();
        SafeFileHandle? stdoutRead = null, stderrRead = null;
        NativeMethods.ProcessInformation info;
        try
        {
            lock (CreateProcessLock)
            {
                SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutWrite = null, stderrWrite = null;
                try
                {
                    var startup = new NativeMethods.StartupInfo { Cb = sizeof(NativeMethods.StartupInfo) };
                    if (captured)
                    {
                        (stdinRead, stdinWrite) = CreatePipe(childReads: true);
                        (stdoutRead, stdoutWrite) = CreatePipe(childReads: false);
                        (stderrRead, stderrWrite) = CreatePipe(childReads: false);
                        startup.Flags = NativeMethods.StartfUseStdHandles;
                        startup.StdInput = stdinRead.DangerousGetHandle();
                        startup.StdOutput = stdoutWrite.DangerousGetHandle();
                        startup.StdError = stderrWrite.DangerousGetHandle();
                    }

                    var flags = NativeMethods.CreateSuspended | NativeMethods.CreateUnicodeEnvironment
                        | (captured ? NativeMethods.CreateNoWindow : NativeMethods.CreateNewConsole);
                    var commandLine = $"{ArgvQuoter.Quote(spec.Command.FileName)} {spec.Command.Arguments}\0".ToCharArray();
                    var environment = EnvironmentBlock(spec.Environment);
                    fixed (char* commandLinePointer = commandLine)
                    fixed (char* environmentPointer = environment)
                    {
                        if (!NativeMethods.CreateProcess(spec.Command.FileName, commandLinePointer, 0, 0, captured, flags,
                                environmentPointer, spec.Command.WorkingDirectory, ref startup, out info))
                            throw new RunException($"Could not start '{spec.Command.FileName}': {new Win32Exception().Message}");
                    }
                }
                finally
                {
                    stdinRead?.Dispose();
                    stdinWrite?.Dispose();
                    stdoutWrite?.Dispose();
                    stderrWrite?.Dispose();
                }
            }

            try
            {
                job.Assign(info.Process);
            }
            catch (Win32Exception ex)
            {
                NativeMethods.TerminateProcess(info.Process, 1);
                NativeMethods.CloseHandle(info.Process);
                throw new RunException($"Could not track '{spec.Command.FileName}': {ex.Message}");
            }
            finally
            {
                NativeMethods.ResumeThread(info.Thread);
                NativeMethods.CloseHandle(info.Thread);
            }
        }
        catch
        {
            stdoutRead?.Dispose();
            stderrRead?.Dispose();
            job.Dispose();
            throw;
        }

        var output = captured
            ? Task.WhenAll(ReadAsync(stdoutRead!, OutputStream.Stdout, onLine), ReadAsync(stderrRead!, OutputStream.Stderr, onLine))
            : Task.CompletedTask;
        return new StartedProcess(job, new SafeProcessHandle(info.Process, ownsHandle: true), info.ProcessId, output);
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreatePipe(bool childReads)
    {
        var attributes = new NativeMethods.SecurityAttributes
        {
            Length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.SecurityAttributes>(),
            InheritHandle = 1,
        };
        if (!NativeMethods.CreatePipe(out var read, out var write, ref attributes, 0))
            throw new Win32Exception();
        NativeMethods.SetHandleInformation(childReads ? write : read, NativeMethods.HandleFlagInherit, 0);
        return (read, write);
    }

    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0', environment.Count == 0 ? 2 : 1).ToString().ToCharArray();
    }

    private static Task ReadAsync(SafeFileHandle pipe, OutputStream stream, Action<string, OutputStream> onLine) =>
        Task.Factory.StartNew(() =>
        {
            using var reader = new FileStream(pipe, FileAccess.Read, bufferSize: 0);
            try
            {
                LineDecoder.ReadLines(reader, line => onLine(line, stream));
            }
            catch (IOException)
            {
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}

internal sealed class StartedProcess : IDisposable
{
    private readonly SafeProcessHandle _process;
    private readonly RegisteredWaitHandle _registration;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartedProcess(JobObject job, SafeProcessHandle process, int id, Task output)
    {
        Job = job;
        _process = process;
        Id = id;
        Output = output;
        _registration = ThreadPool.RegisterWaitForSingleObject(new ProcessWaitHandle(process), (_, _) =>
        {
            NativeMethods.GetExitCodeProcess(_process, out var exitCode);
            _exited.TrySetResult((int)exitCode);
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public JobObject Job { get; }
    public int Id { get; }
    public Task<int> Exited => _exited.Task;
    public Task Output { get; }

    public void Dispose()
    {
        _registration.Unregister(null);
        _process.Dispose();
        Job.Dispose();
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(SafeProcessHandle process) =>
            SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
    }
}
