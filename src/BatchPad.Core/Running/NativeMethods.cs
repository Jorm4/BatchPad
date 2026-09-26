using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BatchPad.Core.Running;

internal static unsafe partial class NativeMethods
{
    public const uint CreateSuspended = 0x4;
    public const uint CreateNewConsole = 0x10;
    public const uint CreateUnicodeEnvironment = 0x400;
    public const uint CreateNoWindow = 0x0800_0000;
    public const uint ExtendedStartupInfoPresent = 0x8_0000;
    public const nuint ProcThreadAttributeHandleList = 0x2_0002;
    public const int StartfUseStdHandles = 0x100;
    public const uint HandleFlagInherit = 0x1;
    public const int JobObjectBasicProcessIdList = 3;
    public const uint WmClose = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfo
    {
        public int Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2;
        public nint StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssocIsDangerous(string association);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreateProcess(string applicationName, char* commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, char* environment,
        string currentDirectory, StartupInfoEx* startupInfo, out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool InitializeProcThreadAttributeList(nint attributeList, int attributeCount, int flags, ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UpdateProcThreadAttribute(nint attributeList, uint flags, nuint attribute, void* value, nuint size,
        nint previousValue, nint returnSize);

    [LibraryImport("kernel32.dll")]
    public static partial void DeleteProcThreadAttributeList(nint attributeList);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(out SafeFileHandle readPipe, out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(SafeProcessHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetOEMCP();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeJobHandle CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AssignProcessToJobObject(SafeJobHandle job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateJobObject(SafeJobHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryInformationJobObject(SafeJobHandle job, int infoClass, void* info, int length, out int returnLength);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}

internal sealed class SafeJobHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
{
    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
