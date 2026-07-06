using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Hoshi.Services.Video;

internal static class MpvNative
{
    internal const string DllName = "libmpv-2";

    private const int MpvFormatString = 1;
    internal const int MpvFormatFlag = 3;
    internal const int MpvFormatInt64 = 4;
    internal const int MpvFormatDouble = 5;
    internal const int MpvEventIdNone = 0;
    internal const int MpvEventIdShutdown = 1;
    internal const int MpvEventIdEndFile = 7;

    private static readonly object ResolverLock = new();
    private static IntPtr s_libraryHandle;
    private static string? s_loadFailure;

    static MpvNative()
    {
        NativeLibrary.SetDllImportResolver(typeof(MpvNative).Assembly, ResolveLibrary);
    }

    internal static IReadOnlyList<string> GetCandidateLibraryPaths()
    {
        var architectureFolder = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

        return
        [
            Path.Combine(AppContext.BaseDirectory, "libmpv", architectureFolder, "libmpv-2.dll"),
            Path.Combine(AppContext.BaseDirectory, "libmpv-2.dll"),
        ];
    }

    internal static string GetLoadDiagnostic()
    {
        if (s_loadFailure != null)
            return s_loadFailure;

        return "Expected libmpv at " + string.Join(" or ", GetCandidateLibraryPaths());
    }

    internal static string ErrorString(int status)
    {
        var ptr = ErrorStringNative(status);
        return Marshal.PtrToStringUTF8(ptr) ?? FormattableString.Invariant($"mpv error {status}");
    }

    internal static int SetOptionStringChecked(IntPtr handle, string name, string value)
    {
        var status = SetOptionString(handle, name, value);
        if (status < 0)
            throw new InvalidOperationException($"libmpv rejected option '{name}': {ErrorString(status)}");

        return status;
    }

    internal static int Command(IntPtr handle, params string[] args)
    {
        var pointers = new IntPtr[args.Length + 1];
        var argv = IntPtr.Zero;
        try
        {
            for (var i = 0; i < args.Length; i++)
                pointers[i] = Marshal.StringToCoTaskMemUTF8(args[i]);

            argv = Marshal.AllocHGlobal(IntPtr.Size * pointers.Length);
            for (var i = 0; i < pointers.Length; i++)
                Marshal.WriteIntPtr(argv, i * IntPtr.Size, pointers[i]);

            return CommandNative(handle, argv);
        }
        finally
        {
            if (argv != IntPtr.Zero)
                Marshal.FreeHGlobal(argv);

            foreach (var pointer in pointers)
                Marshal.FreeCoTaskMem(pointer);
        }
    }

    internal static string FormatSeconds(TimeSpan time) =>
        Math.Max(0, time.TotalSeconds).ToString("0.######", CultureInfo.InvariantCulture);

    private static IntPtr ResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!IsMpvLibraryName(libraryName))
            return IntPtr.Zero;

        lock (ResolverLock)
        {
            if (s_libraryHandle != IntPtr.Zero)
                return s_libraryHandle;

            foreach (var candidate in GetCandidateLibraryPaths())
            {
                if (!File.Exists(candidate))
                    continue;

                if (NativeLibrary.TryLoad(candidate, out s_libraryHandle))
                    return s_libraryHandle;
            }

            if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out s_libraryHandle))
                return s_libraryHandle;

            s_loadFailure = "Unable to load libmpv. Expected libmpv at "
                + string.Join(" or ", GetCandidateLibraryPaths());
            return IntPtr.Zero;
        }
    }

    private static bool IsMpvLibraryName(string libraryName) =>
        string.Equals(libraryName, DllName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(libraryName, "libmpv-2.dll", StringComparison.OrdinalIgnoreCase)
        || string.Equals(libraryName, "mpv-2.dll", StringComparison.OrdinalIgnoreCase);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_create")]
    internal static extern IntPtr Create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_initialize")]
    internal static extern int Initialize(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_terminate_destroy")]
    internal static extern void TerminateDestroy(IntPtr handle);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_option_string")]
    internal static extern int SetOptionString(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property")]
    internal static extern int SetPropertyFlag(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref int data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property")]
    internal static extern int SetPropertyDouble(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        ref double data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_set_property_string")]
    internal static extern int SetPropertyString(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_get_property")]
    internal static extern int GetPropertyDouble(
        IntPtr handle,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        int format,
        out double data);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_wait_event")]
    internal static extern IntPtr WaitEvent(IntPtr handle, double timeout);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_error_string")]
    private static extern IntPtr ErrorStringNative(int error);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "mpv_command")]
    private static extern int CommandNative(IntPtr handle, IntPtr args);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEvent
    {
        public readonly int EventId;
        public readonly int Error;
        public readonly ulong ReplyUserData;
        public readonly IntPtr Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct MpvEventEndFile
    {
        public readonly int Reason;
        public readonly int Error;
        public readonly long PlaylistEntryId;
        public readonly int PlaylistInsertId;
        public readonly int PlaylistInsertNumEntries;
    }
}
