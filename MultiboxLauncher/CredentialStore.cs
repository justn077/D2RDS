using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiboxLauncher;

public static class CredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;

    public static void Save(string target, string username, string secret)
    {
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target is required.", nameof(target));

        var secretBytes = Encoding.Unicode.GetBytes(secret ?? string.Empty);
        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = target,
            Persist = CRED_PERSIST_LOCAL_MACHINE,
            CredentialBlobSize = (uint)secretBytes.Length,
            UserName = username ?? string.Empty
        };

        credential.CredentialBlob = Marshal.AllocHGlobal(secretBytes.Length);
        try
        {
            Marshal.Copy(secretBytes, 0, credential.CredentialBlob, secretBytes.Length);
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to write credential.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
        }
    }

    public static (string Username, string Secret)? Read(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return null;

        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var secret = "";
            if (credential.CredentialBlob != IntPtr.Zero && credential.CredentialBlobSize > 0)
            {
                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, (int)credential.CredentialBlobSize);
                secret = Encoding.Unicode.GetString(bytes);
            }

            return (credential.UserName ?? "", secret);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public static void Delete(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        CredDelete(target, CRED_TYPE_GENERIC, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredWrite([In] ref CREDENTIAL userCredential, [In] uint flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree([In] IntPtr buffer);
}
