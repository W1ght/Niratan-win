using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Niratan.Services.Video;

internal sealed class WindowsCredentialVideoMetadataStore : IVideoMetadataCredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private static readonly string[] AllowedProviders = ["tmdb", "tvdb", "jimaku", "anidb"];
    private static readonly string[] AllowedSecretNames = ["token", "pin", "username", "password"];

    public Task<string?> ReadAsync(string providerId, string secretName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = GetTarget(providerId, secretName);
        if (!CredRead(target, CredTypeGeneric, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return Task.FromResult<string?>(null);
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
                return Task.FromResult<string?>(null);
            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public Task WriteAsync(
        string providerId,
        string secretName,
        string value,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ct.ThrowIfCancellationRequested();
        var target = GetTarget(providerId, secretName);
        var bytes = Encoding.UTF8.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = providerId,
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

    public Task DeleteAsync(string providerId, string secretName, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var target = GetTarget(providerId, secretName);
        if (!CredDelete(target, CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
                throw new Win32Exception(error);
        }
        return Task.CompletedTask;
    }

    private static string GetTarget(string providerId, string secretName)
    {
        if (!AllowedProviders.Contains(providerId, StringComparer.OrdinalIgnoreCase)
            || !AllowedSecretNames.Contains(secretName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Unsupported video metadata credential target.");
        }
        return $"Niratan.VideoMetadata.{providerId.ToLowerInvariant()}.{secretName.ToLowerInvariant()}";
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredReadW")]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredWriteW")]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CredDeleteW")]
    private static extern bool CredDelete(string targetName, uint type, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }
}
