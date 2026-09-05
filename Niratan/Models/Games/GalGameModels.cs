using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using Niratan.Helpers;

namespace Niratan.Models.Games;

public enum GalGamePlayStatus
{
    Unset = 0,
    WantToPlay = 1,
    Played = 2,
    Playing = 3,
    OnHold = 4,
    Dropped = 5,
}

public enum GalHookSessionPhase
{
    Idle,
    Resolving,
    Launching,
    Attaching,
    Injecting,
    OpeningIpc,
    WaitingSignals,
    Running,
    Degraded,
    Stopping,
    Error,
}

public enum GalHookFailureReason
{
    UnsupportedPlatform,
    HelperMissing,
    InvalidTarget,
    InjectionFailed,
    IpcUnavailable,
    Cancelled,
}

public sealed record GalGameEntry
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ExePath { get; init; }
    public required string Workdir { get; init; }
    public string LaunchArgs { get; init; } = string.Empty;
    public string UpscalingMode { get; init; } = string.Empty;
    public string JapaneseLocaleMode { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string? CoverPath { get; init; }
    public DateTimeOffset AddedAt { get; init; }
    public GalGamePlayStatus PlayStatus { get; init; } = GalGamePlayStatus.Unset;
    public int SortOrder { get; init; }
    public int TotalPlaySeconds { get; init; }
    public int SessionCount { get; init; }
    public long LastPlayedMs { get; init; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? GalGameLibraryFunctions.NameFromExe(ExePath)
        : Name;

    [JsonIgnore]
    public IReadOnlyList<string> LaunchArgumentTokens =>
        GalGameLibraryFunctions.ParseLaunchArguments(LaunchArgs);

    public GalGameEntry WithLaunchArgs(string launchArgs) => this with
    {
        LaunchArgs = launchArgs ?? string.Empty,
    };
}

public sealed record GalGameLibraryDocument
{
    public int SchemaVersion { get; init; } = 1;
    public List<GalGameEntry> Games { get; init; } = [];
}

public sealed record GalHookSessionState
{
    public GalHookSessionPhase Phase { get; init; } = GalHookSessionPhase.Idle;
    public int? GamePid { get; init; }
    public string? LaunchExe { get; init; }
    public string? InjectorPath { get; init; }
    public string? Architecture { get; init; }
    public string? LastError { get; init; }
    public string? Detail { get; init; }
    public GalGameIpcSnapshot? Ipc { get; init; }

    [JsonIgnore]
    public bool IsActive => Phase is not GalHookSessionPhase.Idle
        and not GalHookSessionPhase.Error;
}

public sealed record GalGameIpcSnapshot
{
    public const uint SharedMagic = 0x31485648;
    public const uint SharedVersion = 21;
    public const uint StableIpcVersion = 1;

    public required int ProcessId { get; init; }
    public required uint Magic { get; init; }
    public required uint Version { get; init; }
    public required uint IpcProtocolVersion { get; init; }
    public required uint SampleRate { get; init; }
    public required uint Channels { get; init; }
    public required uint BitsPerSample { get; init; }
    public required uint RingCapacity { get; init; }
    public required uint Hooked { get; init; }
    public required uint TextHooked { get; init; }
    public required uint LunaActive { get; init; }
    public required uint HookDiagnostics { get; init; }
    public uint XAudioDiagnostics { get; init; }
    public uint XAudioDiagnostics2 { get; init; }
    public required ulong TotalWritten { get; init; }
    public required ulong TextWriteCount { get; init; }
    public required ulong ClipWriteCount { get; init; }

    public uint BlockAlign { get; init; }
    public bool IsFloat { get; init; }
    public uint TextRegionOffset { get; init; }
    public uint ClipRegionOffset { get; init; }
    public ulong SelectedTextThreadId { get; init; }
    public uint ReservedLunaDiagnostics { get; init; }
    public uint ReservedHookDiagnostics { get; init; }
    public uint LoopbackRingOffset { get; init; }
    public uint LoopbackRingCapacity { get; init; }
    public uint LoopbackSampleRate { get; init; }
    public uint LoopbackChannels { get; init; }
    public uint LoopbackBitsPerSample { get; init; }
    public uint LoopbackDiagnostics { get; init; }
    public ulong LoopbackTotalWritten { get; init; }
    public ulong LoopbackMarkerCount { get; init; }
    public uint TextLaneCount { get; init; }
    public uint TextLaneSlotCount { get; init; }
    public ulong TextLaneRecycleCount { get; init; }
    public ulong TextLaneOverflowCount { get; init; }
    public uint LookupRegionOffset { get; init; }
    public uint LookupBitmapBytes { get; init; }
    public uint LookupFrameCount { get; init; }
    public uint LookupInputSlotCount { get; init; }
    public ulong LookupHitCount { get; init; }
    public ulong LookupFrameCountWritten { get; init; }
    public ulong LookupInputCount { get; init; }
    public uint LookupEnabled { get; init; }
    public uint LookupDiagnostics { get; init; }
    public ulong LookupFrameAppliedSequence { get; init; }

    [JsonIgnore]
    public bool IsCompatible => Magic == SharedMagic
        && Version == SharedVersion
        && IpcProtocolVersion == StableIpcVersion;

    [JsonIgnore]
    public bool HasAudioFormat => SampleRate > 0 && Channels > 0 && BitsPerSample > 0;

    [JsonIgnore]
    public bool HasLoopbackAudio => LoopbackSampleRate > 0
        && LoopbackChannels > 0
        && LoopbackBitsPerSample == 16
        && LoopbackTotalWritten > 0
        && LoopbackMarkerCount > 0;

    [JsonIgnore]
    public bool HasIngameLookup => IsCompatible
        && LookupRegionOffset > 0
        && LookupBitmapBytes > 0
        && LookupFrameCount >= 2
        && LookupInputSlotCount > 0;
}

public sealed record GalGameLookupHit
{
    public required int ProcessId { get; init; }
    public required ulong Sequence { get; init; }
    public required uint CharacterIndex { get; init; }
    public required uint CharacterCount { get; init; }
    public required int GlyphX { get; init; }
    public required int GlyphY { get; init; }
    public required int GlyphWidth { get; init; }
    public required int GlyphHeight { get; init; }
    public required int ViewWidth { get; init; }
    public required int ViewHeight { get; init; }
    public required string Line { get; init; }
}

public sealed record GalGameLookupInput
{
    public required ulong Sequence { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
    public required uint Kind { get; init; }
    public required int Wheel { get; init; }
    public required uint Keys { get; init; }
}

public sealed record GalGameLookupCardFrame
{
    public required ulong HitSequence { get; init; }
    public required ulong FrameSequence { get; init; }
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int Pitch { get; init; }
    public int AnchorX { get; init; }
    public int AnchorY { get; init; }
    public int HighlightStart { get; init; }
    public int HighlightLength { get; init; }
}

public sealed record GalGameTextLine
{
    public int ProcessId { get; set; }
    public ulong Sequence { get; set; }
    public ulong TimestampMs { get; set; }
    public ulong ThreadId { get; set; }
    public ulong FaceId { get; set; }
    public uint SourceKind { get; set; }
    public uint EventKind { get; set; }
    public string Text { get; set; } = string.Empty;
    public string HookName { get; set; } = string.Empty;
    public string HookCode { get; set; } = string.Empty;

    [JsonIgnore]
    public string Id => $"{ProcessId}:{Sequence}";

    [JsonIgnore]
    public bool IsThreadDiscovered => EventKind != 0;

    [JsonIgnore]
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    [JsonIgnore]
    public string SourceLabel => SourceKind switch
    {
        1 => "GDI",
        2 => "LunaHook",
        3 => "Unity TMP",
        4 => "Siglus",
        6 => ResourceStringHelper.GetString(
            "GamesTextSourceKirikiriTextRender",
            "KiriKiri TextRender"),
        _ => "Hook",
    };
}

public sealed record GalGameThreadPreview
{
    public ulong ThreadId { get; set; }
    public ulong Sequence { get; set; }
    public ulong TimestampMs { get; set; }
    public ulong LineCount { get; set; }
    public ulong ArtifactCount { get; set; }
    public uint EventFlags { get; set; }
    public string Text { get; set; } = string.Empty;

    [JsonIgnore]
    public string PreviewText { get; set; } = string.Empty;

    [JsonIgnore]
    public bool IsArtifact => (EventFlags & 1u) != 0;

    [JsonIgnore]
    public string DisplayName => ThreadId == 0
        ? "Current capture lane"
        : $"Thread {ThreadId:x}";
}

public sealed record GalGameAudioCapture
{
    public required byte[] Pcm { get; init; }
    public required int SampleRate { get; init; }
    public required int Channels { get; init; }
    public required int BitsPerSample { get; init; }
    public required bool IsFloat { get; init; }
    public ulong TimestampMs { get; init; }
    public ulong SourcePtr { get; init; }

    [JsonIgnore]
    public bool IsValid => Pcm.Length > 0
        && SampleRate > 0
        && Channels > 0
        && BitsPerSample > 0;
}

public sealed record GalHookOperationResult(
    bool Success,
    GalHookFailureReason? FailureReason,
    string? Detail,
    GalHookSessionState State);

public static class GalGameLibraryFunctions
{
    public static GalGameEntry NewFromExe(
        string exePath,
        DateTimeOffset? now = null,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exePath);
        var fullPath = Path.GetFullPath(exePath);
        var addedAt = now ?? DateTimeOffset.UtcNow;
        return new GalGameEntry
        {
            Id = addedAt.ToUnixTimeMilliseconds().ToString(),
            Name = string.IsNullOrWhiteSpace(name) ? NameFromExe(fullPath) : name.Trim(),
            ExePath = fullPath,
            Workdir = Path.GetDirectoryName(fullPath) ?? string.Empty,
            AddedAt = addedAt,
        };
    }

    public static string NameFromExe(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return string.Empty;
        var fileName = exePath.Replace('/', '\\');
        var slash = fileName.LastIndexOf('\\');
        fileName = slash >= 0 ? fileName[(slash + 1)..] : fileName;
        return Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(fileName)
            : fileName;
    }

    public static GalGameEntry? FindByExePath(
        IEnumerable<GalGameEntry> games,
        string exePath)
    {
        var key = PathKey(exePath);
        return string.IsNullOrEmpty(key)
            ? null
            : games.FirstOrDefault(game => PathKey(game.ExePath) == key);
    }

    public static IReadOnlyList<string> FilterNewExes(
        IEnumerable<GalGameEntry> existing,
        IEnumerable<string> dropped)
    {
        var seen = existing
            .Select(game => PathKey(game.ExePath))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var path in dropped)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;
            var key = PathKey(path);
            if (seen.Add(key))
                result.Add(path);
        }
        return result;
    }

    public static IReadOnlyList<string> ParseLaunchArguments(string raw)
    {
        var input = raw ?? string.Empty;
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inToken = false;
        var inQuotes = false;
        for (var i = 0; i < input.Length;)
        {
            var c = input[i];
            if (!inQuotes && (c is ' ' or '\t' or '\r' or '\n'))
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
                i++;
                continue;
            }

            inToken = true;
            if (c == '\\')
            {
                var slashCount = 0;
                while (i < input.Length && input[i] == '\\')
                {
                    slashCount++;
                    i++;
                }

                if (i < input.Length && input[i] == '"')
                {
                    current.Append('\\', slashCount / 2);
                    if (slashCount % 2 == 0)
                        inQuotes = !inQuotes;
                    else
                        current.Append('"');
                    i++;
                }
                else
                {
                    current.Append('\\', slashCount);
                }
                continue;
            }

            if (c == '"')
            {
                if (inQuotes && i + 1 < input.Length && input[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                    continue;
                }
                inQuotes = !inQuotes;
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (inToken)
            tokens.Add(current.ToString());
        return tokens;
    }

    private static string PathKey(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : path.Trim().Replace('/', '\\').ToLowerInvariant();
}
