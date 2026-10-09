using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace PerforceLauncher.Core.Services;

public interface ICredentialStore
{
    string? Read(string target);
    void Save(string target, string userName, string secret);
    void Delete(string target);
    IReadOnlyList<string> List(string prefix);
}

/// <summary>
/// Диспетчер учётных данных Windows (Generic credentials). Секрет шифруется Windows (DPAPI) под текущей учётной
/// записью и хранится только на этой машине; запись видна и удаляется в «Панель управления → Диспетчер учётных данных».
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerateW(string? filter, uint flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);

    public string? Read(string target)
    {
        if (!CredReadW(target, CredTypeGeneric, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            return cred.CredentialBlob == IntPtr.Zero ? null
                : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public void Save(string target, string userName, string secret)
    {
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = "Perforce Launcher: automatic Perforce sign-in",
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(secret.Length * 2),
                Persist = CredPersistLocalMachine,
                UserName = userName,
            };
            if (!CredWriteW(ref cred, 0))
                throw new InvalidOperationException($"Windows Credential Manager error {Marshal.GetLastWin32Error()}.");
        }
        finally
        {
            // затираем копию пароля в неуправляемой памяти
            Marshal.Copy(new byte[secret.Length * 2], 0, blob, secret.Length * 2);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete(string target) => CredDeleteW(target, CredTypeGeneric, 0);

    public IReadOnlyList<string> List(string prefix)
    {
        if (!CredEnumerateW(prefix + "*", 0, out var count, out var ptr)) return Array.Empty<string>();
        try
        {
            var result = new List<string>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(ptr, i * IntPtr.Size);
                result.Add(Marshal.PtrToStructure<Credential>(item).TargetName);
            }
            return result;
        }
        finally { CredFree(ptr); }
    }
}
