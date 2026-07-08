using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hoshi.Models.Dictionary;

namespace Hoshi.Services.Dictionary;

/// <summary>
/// P/Invoke bindings for the hoshidicts C API wrapper DLL.
/// </summary>
internal static class HoshiDictsNative
{
    private const string DllName = "hoshidicts_c_api";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /* --------------------------------------------------------------------- */
    /* P/Invoke declarations                                                 */
    /* --------------------------------------------------------------------- */

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hoshi_session_create();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hoshi_session_destroy(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hoshi_session_rebuild(
        IntPtr session,
        IntPtr[] term_paths, int term_count,
        IntPtr[] freq_paths, int freq_count,
        IntPtr[] pitch_paths, int pitch_count);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void hoshi_session_rebuild_with_language(
        IntPtr session,
        IntPtr[] term_paths, int term_count,
        IntPtr[] freq_paths, int freq_count,
        IntPtr[] pitch_paths, int pitch_count,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string language_id);

    internal static void HoshiSessionRebuild(
        IntPtr session,
        IReadOnlyList<string> termPaths,
        IReadOnlyList<string> freqPaths,
        IReadOnlyList<string> pitchPaths) =>
        HoshiSessionRebuild(session, termPaths, freqPaths, pitchPaths, "ja");

    internal static void HoshiSessionRebuild(
        IntPtr session,
        IReadOnlyList<string> termPaths,
        IReadOnlyList<string> freqPaths,
        IReadOnlyList<string> pitchPaths,
        string languageId)
    {
        var termPtrs = AllocUtf8Array(termPaths);
        var freqPtrs = AllocUtf8Array(freqPaths);
        var pitchPtrs = AllocUtf8Array(pitchPaths);
        try
        {
            hoshi_session_rebuild_with_language(
                session,
                termPtrs, termPtrs.Length,
                freqPtrs, freqPtrs.Length,
                pitchPtrs, pitchPtrs.Length,
                string.IsNullOrWhiteSpace(languageId) ? "ja" : languageId);
        }
        finally
        {
            FreeUtf8Array(termPtrs);
            FreeUtf8Array(freqPtrs);
            FreeUtf8Array(pitchPtrs);
        }
    }

    private static IntPtr[] AllocUtf8Array(IReadOnlyList<string> paths)
    {
        var ptrs = new IntPtr[paths.Count];
        for (var i = 0; i < paths.Count; i++)
            ptrs[i] = Marshal.StringToCoTaskMemUTF8(paths[i]);
        return ptrs;
    }

    private static void FreeUtf8Array(IntPtr[] ptrs)
    {
        foreach (var ptr in ptrs)
            Marshal.FreeCoTaskMem(ptr);
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hoshi_import(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string zip_path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string output_dir);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hoshi_lookup(
        IntPtr session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        int max_results,
        int scan_length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hoshi_get_styles(IntPtr session);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hoshi_get_media_file(
        IntPtr session,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string dict_name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string media_path,
        out int out_size);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr hoshi_debug_hash(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hoshi_string_free(IntPtr str);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void hoshi_buffer_free(IntPtr buffer);

    /* --------------------------------------------------------------------- */
    /* Safe helpers                                                          */
    /* --------------------------------------------------------------------- */

    internal static string? ReadStringAndFree(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(ptr);
        }
        finally
        {
            hoshi_string_free(ptr);
        }
    }

    internal static byte[]? ReadBufferAndFree(IntPtr ptr, int size)
    {
        if (ptr == IntPtr.Zero || size <= 0)
            return null;
        try
        {
            var buffer = new byte[size];
            Marshal.Copy(ptr, buffer, 0, size);
            return buffer;
        }
        finally
        {
            hoshi_buffer_free(ptr);
        }
    }

    internal static List<DictionaryLookupResult> DeserializeLookupResults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return JsonSerializer.Deserialize<List<DictionaryLookupResult>>(json, JsonOptions) ?? [];
    }

    internal static List<DictionaryStyle> DeserializeStyles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];
        return JsonSerializer.Deserialize<List<DictionaryStyle>>(json, JsonOptions) ?? [];
    }
}

/// <summary>
/// JSON model for hoshi_import result.
/// </summary>
internal sealed record NativeImportResultJson(
    bool Success,
    string Title = "",
    long TermCount = 0,
    long MetaCount = 0,
    long FreqCount = 0,
    long PitchCount = 0,
    long MediaCount = 0,
    bool TimedOut = false,
    List<string>? Errors = null
);
