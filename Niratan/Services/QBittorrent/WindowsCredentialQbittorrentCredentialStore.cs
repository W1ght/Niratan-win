using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.QBittorrent;

namespace Niratan.Services.QBittorrent;

public sealed class WindowsCredentialQbittorrentCredentialStore : IQbittorrentCredentialStore
{
    private const string TargetName = "Niratan.QBittorrent.Credentials";
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public bool HasCredentials => ReadCredential() is not null;

    public Task<QbittorrentCredentials?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(ReadCredential());
    }

    public Task SaveAsync(QbittorrentCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(credentials.Username)
            && string.IsNullOrWhiteSpace(credentials.Password)
            && string.IsNullOrWhiteSpace(credentials.ApiKey))
            return DeleteAsync(ct);

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(credentials, JsonOptions));
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var native = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = TargetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = credentials.Username,
            };

            if (!CredWrite(ref native, 0))
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
        if (!CredDelete(TargetName, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error);
        }

        return Task.CompletedTask;
    }

    private static QbittorrentCredentials? ReadCredential()
    {
        if (!CredRead(TargetName, CredTypeGeneric, 0, out var credentialPtr))
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
            return JsonSerializer.Deserialize<QbittorrentCredentials>(
                Encoding.UTF8.GetString(bytes),
                JsonOptions);
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
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

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
