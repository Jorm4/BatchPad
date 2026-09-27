using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace BatchPad.Core.Running;

/// <summary>Secrets as generic credentials in Windows Credential Manager, saved for this user on this machine.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe partial class CredentialManagerSecretStore : ISecretStore
{
    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;
    private const int NotFound = 1168;
    private const int InvalidParameter = 87;
    private const int MaxValueLength = 1280;
    private const int MaxRootLength = 255;

    public (string Value, string? Root)? Get(string target)
    {
        if (!CredRead(target, GenericType, 0, out var credential))
            return Marshal.GetLastPInvokeError() == NotFound ? null : throw Failure("read", target);
        try
        {
            return (Encoding.Unicode.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize), RootOf(credential));
        }
        finally
        {
            CredFree(credential);
        }
    }

    public void Set(string target, string value, string? root)
    {
        if (value.Length > MaxValueLength)
            throw new Win32Exception(InvalidParameter,
                $"Could not save '{target}': Windows Credential Manager holds at most {MaxValueLength} characters, and this value has {value.Length}.");
        if (root?.Length > MaxRootLength)
            throw new Win32Exception(InvalidParameter,
                $"Could not save '{target}': its folder path is longer than the {MaxRootLength} characters Windows Credential Manager keeps.");
        var blob = Encoding.Unicode.GetBytes(value);
        fixed (char* targetName = target)
        fixed (char* comment = root)
        fixed (byte* bytes = blob)
        {
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = targetName,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = bytes,
                Persist = PersistLocalMachine,
            };
            if (!CredWrite(&credential, 0))
                throw Failure("save", target);
        }
    }

    public bool Remove(string target)
    {
        if (CredDelete(target, GenericType, 0))
            return true;
        return Marshal.GetLastPInvokeError() == NotFound ? false : throw Failure("remove", target);
    }

    public IReadOnlyList<(string Target, string? Root)> List(string prefix)
    {
        if (!CredEnumerate(prefix + "*", 0, out var count, out var credentials))
            return Marshal.GetLastPInvokeError() == NotFound ? [] : throw Failure("list", prefix + "*");
        try
        {
            var targets = new List<(string, string?)>();
            for (var i = 0; i < count; i++)
            {
                var credential = credentials[i];
                if (credential->Type == GenericType && new string(credential->TargetName) is var target
                    && target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    targets.Add((target, RootOf(credential)));
            }
            return targets;
        }
        finally
        {
            CredFree(credentials);
        }
    }

    private static string? RootOf(Credential* credential) => credential->Comment is null ? null : new string(credential->Comment);

    private static Win32Exception Failure(string action, string target)
    {
        var error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"Could not {action} '{target}' in Windows Credential Manager: {new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public char* TargetName;
        public char* Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public byte* CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public char* TargetAlias;
        public char* UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint flags, out Credential* credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(Credential* credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredEnumerateW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredEnumerate(string filter, uint flags, out int count, out Credential** credentials);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(void* buffer);
}
