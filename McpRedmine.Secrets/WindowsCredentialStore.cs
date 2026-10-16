using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace McpRedmine.Secrets;

/// <summary>Windows Credential Manager (generic credentials, scoped to the current user and machine).</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ISecretStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public string Description => "Windows Credential Manager";

    public bool StoresPlaintext => false;

    public ApiKey? Get(string account)
    {
        if (!CredReadW(TargetName(account), CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            return error == ErrorNotFound ? null : throw new InvalidOperationException($"Credential Manager read failed (Win32 error {error}).");
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
                return null;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var text = Encoding.UTF8.GetString(bytes);
            Array.Clear(bytes);
            return string.IsNullOrWhiteSpace(text) ? null : new ApiKey(text);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Set(string account, ApiKey key)
    {
        var blob = Encoding.UTF8.GetBytes(key.Reveal());
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        var targetPointer = Marshal.StringToHGlobalUni(TargetName(account));
        var userPointer = Marshal.StringToHGlobalUni(account);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Persist = CredPersistLocalMachine,
                UserName = userPointer,
            };

            if (!CredWriteW(in credential, 0))
                throw new InvalidOperationException($"Credential Manager write failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            Array.Clear(blob);
            Marshal.FreeHGlobal(blobPointer);
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(userPointer);
        }
    }

    public bool Remove(string account)
    {
        if (CredDeleteW(TargetName(account), CredTypeGeneric, 0))
            return true;

        var error = Marshal.GetLastWin32Error();
        return error == ErrorNotFound ? false : throw new InvalidOperationException($"Credential Manager delete failed (Win32 error {error}).");
    }

    internal static string TargetName(string account) => $"RedmineMcp:{account}";

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(in Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
