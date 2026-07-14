using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Sync;

namespace Niratan.Services.Sync;

public sealed class WindowsCredentialGoogleDriveCredentialStore : IGoogleDriveCredentialStore
{
    private const string TargetName = "Niratan.TtuSync.GoogleDriveCredentials";
    private const string LegacyTargetName = "Hoshi.TtuSync.GoogleDriveCredentials";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool HasCredentials => ReadCredential(TargetName) != null
        || ReadCredential(LegacyTargetName) != null;

    public Task<GoogleDriveCredentials?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ReadCredential(TargetName) ?? ReadCredential(LegacyTargetName));
    }

    public Task SaveAsync(GoogleDriveCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ct.ThrowIfCancellationRequested();

        var json = JsonSerializer.Serialize(credentials, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = "Niratan",
            };

            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        DeleteCredential(TargetName);
        DeleteCredential(LegacyTargetName);

        return Task.CompletedTask;
    }

    private static void DeleteCredential(string targetName)
    {
        if (!CredDelete(targetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error);
        }
    }

    private static GoogleDriveCredentials? ReadCredential(string targetName)
    {
        if (!CredRead(targetName, CredTypeGeneric, 0, out var credentialPtr))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;

            throw new Win32Exception(error);
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPtr);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return null;

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var json = Encoding.UTF8.GetString(bytes);
            return JsonSerializer.Deserialize<GoogleDriveCredentials>(json, JsonOptions);
        }
        finally
        {
            CredFree(credentialPtr);
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(
        string targetName,
        uint type,
        uint reservedFlag,
        out IntPtr credentialPtr);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(
        ref NativeCredential credential,
        uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(
        string targetName,
        uint type,
        uint flags);

    [DllImport("advapi32.dll", SetLastError = false, EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }
}
