using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using BatchPad.Core.Model;
using Microsoft.Win32.SafeHandles;

namespace BatchPad.Core.Running;

public sealed record RunSpec(CommandLine Command, IReadOnlyDictionary<string, string> Environment)
{
    public ConsoleMode Console { get; init; } = ConsoleMode.Captured;
    public TimeSpan? Timeout { get; init; }
}

/// <summary>Starts runs inside Job Objects (§4).</summary>
internal static class ProcessRunner
{
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

    /// <exception cref="RunException">The process could not be started or tracked.</exception>
    internal static StartedProcess Launch(RunSpec spec, Action<string, OutputStream> onLine)
    {
        try
        {
            return LaunchInJob(spec, onLine);
        }
        catch (Win32Exception ex)
        {
            throw new RunException($"Could not start '{spec.Command.FileName}': {ex.Message}");
        }
    }

    private static unsafe StartedProcess LaunchInJob(RunSpec spec, Action<string, OutputStream> onLine)
    {
        var captured = spec.Console == ConsoleMode.Captured;
        var job = new JobObject();
        SafeFileHandle? stdoutRead = null, stderrRead = null;
        NativeMethods.ProcessInformation info;
        var inherited = stackalloc nint[3];
        try
        {
            SafeFileHandle? stdinRead = null, stdinWrite = null, stdoutWrite = null, stderrWrite = null;
            nint attributeList = 0;
            try
            {
                var startup = new NativeMethods.StartupInfoEx { StartupInfo = { Cb = sizeof(NativeMethods.StartupInfo) } };
                var flags = NativeMethods.CreateSuspended | NativeMethods.CreateUnicodeEnvironment;
                if (captured)
                {
                    (stdinRead, stdinWrite) = CreatePipe(childReads: true);
                    (stdoutRead, stdoutWrite) = CreatePipe(childReads: false);
                    (stderrRead, stderrWrite) = CreatePipe(childReads: false);
                    inherited[0] = stdinRead.DangerousGetHandle();
                    inherited[1] = stdoutWrite.DangerousGetHandle();
                    inherited[2] = stderrWrite.DangerousGetHandle();
                    attributeList = InheritOnly(inherited, 3);
                    startup.StartupInfo.Cb = sizeof(NativeMethods.StartupInfoEx);
                    startup.StartupInfo.Flags = NativeMethods.StartfUseStdHandles;
                    startup.StartupInfo.StdInput = inherited[0];
                    startup.StartupInfo.StdOutput = inherited[1];
                    startup.StartupInfo.StdError = inherited[2];
                    startup.AttributeList = attributeList;
                    flags |= NativeMethods.CreateNoWindow | NativeMethods.ExtendedStartupInfoPresent;
                }
                else
                {
                    flags |= NativeMethods.CreateNewConsole;
                }

                var commandLine = $"{ArgvQuoter.Quote(spec.Command.FileName)} {spec.Command.Arguments}\0".ToCharArray();
                var environment = EnvironmentBlock(spec.Environment);
                fixed (char* commandLinePointer = commandLine)
                fixed (char* environmentPointer = environment)
                {
                    if (!NativeMethods.CreateProcess(spec.Command.FileName, commandLinePointer, 0, 0, captured, flags,
                            environmentPointer, spec.Command.WorkingDirectory, &startup, out info))
                        throw new RunException($"Could not start '{spec.Command.FileName}': {new Win32Exception().Message}");
                }
            }
            finally
            {
                if (attributeList != 0)
                {
                    NativeMethods.DeleteProcThreadAttributeList(attributeList);
                    NativeMemory.Free((void*)attributeList);
                }
                stdinRead?.Dispose();
                stdinWrite?.Dispose();
                stdoutWrite?.Dispose();
                stderrWrite?.Dispose();
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

    /// <summary>An attribute list that lets the child inherit <paramref name="handles"/> and nothing else, such as another launch's pipes.</summary>
    private static unsafe nint InheritOnly(nint* handles, int count)
    {
        nuint size = 0;
        NativeMethods.InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = (nint)NativeMemory.Alloc(size);
        if (NativeMethods.InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            if (NativeMethods.UpdateProcThreadAttribute(list, 0, NativeMethods.ProcThreadAttributeHandleList, handles,
                    (nuint)(count * sizeof(nint)), 0, 0))
                return list;
            NativeMethods.DeleteProcThreadAttributeList(list);
        }
        var error = new Win32Exception();
        NativeMemory.Free((void*)list);
        throw error;
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

    private static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var block = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(v => v.Key, StringComparer.OrdinalIgnoreCase))
            block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0', environment.Count == 0 ? 2 : 1).ToString();
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
