using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;
using System.Threading;
using Niratan.Models.Games;

namespace Niratan.Services.Games;

internal sealed class GalGameIpcReader
{
    private const uint FileMapRead = 0x0004;
    private const uint FileMapWrite = 0x0002;
    private const int SharedHeaderSize = 272;
    private const int TextLaneSize = 24;
    private const int TextSlotBytes = 2048;
    private const int TextLaneCount = 64;
    private const int TextLaneSlotCount = 8;
    private const int TextSlotHeaderSize = 96 + (64 * 1) + (128 * 2);
    private const int TextHookNameOffset = 96;
    private const int TextHookCodeOffset = TextHookNameOffset + 64;
    private const int TextPayloadOffset = TextHookCodeOffset + (128 * 2);
    private const int VoiceClipSize = 64;
    private const int VoiceClipSourceOffset = 52;
    private const int ThreadPreviewSlotSize = 432;
    private const int ThreadPreviewTextOffset = 48;
    private const int ThreadPreviewTextChars = 192;
    private const int MagicOffset = 0;
    private const int VersionOffset = 4;
    private const int IpcProtocolVersionOffset = 8;
    private const int SampleRateOffset = 24;
    private const int ChannelsOffset = 28;
    private const int BitsPerSampleOffset = 32;
    private const int RingCapacityOffset = 40;
    private const int HookedOffset = 52;
    private const int TextHookedOffset = 60;
    private const int TotalWrittenOffset = 64;
    private const int TextWriteCountOffset = 80;
    private const int ClipWriteCountOffset = 88;
    private const int LunaActiveOffset = 104;
    private const int HookDiagnosticsOffset = 112;
    private const int BlockAlignOffset = 44;
    private const int IsFloatOffset = 36;
    private const int WritePosOffset = 48;
    private const int TextRegionOffset = 72;
    private const int ClipRegionOffset = 76;
    private const int SelectedTextThreadIdOffset = 96;
    private const int ReservedLunaDiagnosticsOffset = 108;
    private const int ReservedHookDiagnosticsOffset = 116;
    private const int LoopbackRingOffset = 21120;
    private const int LoopbackRingCapacityOffset = 21124;
    private const int LoopbackMarkerOffset = 21128;
    private const int LoopbackMarkerSlotCountOffset = 21132;
    private const int LoopbackSampleRateOffset = 21136;
    private const int LoopbackChannelsOffset = 21140;
    private const int LoopbackBitsPerSampleOffset = 21144;
    private const int LoopbackDiagnosticsOffset = 21156;
    private const int LoopbackTotalWrittenOffset = 21160;
    private const int LoopbackMarkerCountOffset = 21168;
    private const int LoopbackMarkerSize = 24;
    private const int TextLaneCountOffset = 21192;
    private const int TextLaneSlotCountOffset = 21196;
    private const int TextLaneRecycleCountOffset = 21200;
    private const int TextLaneOverflowCountOffset = 21208;
    private const int LookupRegionOffset = 21216;
    private const int LookupBitmapBytesOffset = 21220;
    private const int LookupFrameCountOffset = 21224;
    private const int LookupInputSlotCountOffset = 21228;
    private const int LookupHitCountOffset = 21232;
    private const int LookupFrameCountWrittenOffset = 21240;
    private const int LookupInputCountOffset = 21248;
    private const int LookupEnabledOffset = 21256;
    private const int LookupDiagnosticsOffset = 21260;
    private const int LookupFrameAppliedSequenceOffset = 21264;
    private const int LookupHitBytes = 1072;
    private const int LookupInputBytes = 32;
    private const int LookupFrameBytes = 64;
    private const int LookupHitLineOffset = 48;
    private const int LookupInputRegionOffset = LookupHitBytes;
    private const int LookupFrameReadyOffset = 52;
    private const int LookupFrameByteLengthOffset = 48;
    private const int GalGameLookupHitLineBytes = 1024;

    public GalGameIpcSnapshot? TryRead(int processId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
            return null;

        var mappingName = $"Local\\FushiVoiceHook_{processId}";
        var mapping = OpenFileMapping(FileMapRead, false, mappingName);
        if (mapping == IntPtr.Zero)
            return null;

        try
        {
            var view = MapViewOfFile(mapping, FileMapRead, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
                return null;
            try
            {
                return new GalGameIpcSnapshot
                {
                    ProcessId = processId,
                    Magic = ReadUInt32(view, MagicOffset),
                    Version = ReadUInt32(view, VersionOffset),
                    IpcProtocolVersion = ReadUInt32(view, IpcProtocolVersionOffset),
                    SampleRate = ReadUInt32(view, SampleRateOffset),
                    Channels = ReadUInt32(view, ChannelsOffset),
                    BitsPerSample = ReadUInt32(view, BitsPerSampleOffset),
                    RingCapacity = ReadUInt32(view, RingCapacityOffset),
                    BlockAlign = ReadUInt32(view, BlockAlignOffset),
                    IsFloat = ReadUInt32(view, IsFloatOffset) != 0,
                    Hooked = ReadUInt32(view, HookedOffset),
                    TextHooked = ReadUInt32(view, TextHookedOffset),
                    TotalWritten = ReadUInt64(view, TotalWrittenOffset),
                    TextWriteCount = ReadUInt64(view, TextWriteCountOffset),
                    ClipWriteCount = ReadUInt64(view, ClipWriteCountOffset),
                    LunaActive = ReadUInt32(view, LunaActiveOffset),
                    HookDiagnostics = ReadUInt32(view, HookDiagnosticsOffset),
                    TextRegionOffset = ReadUInt32(view, TextRegionOffset),
                    ClipRegionOffset = ReadUInt32(view, ClipRegionOffset),
                    SelectedTextThreadId = ReadUInt64(view, SelectedTextThreadIdOffset),
                    ReservedLunaDiagnostics = ReadUInt32(view, ReservedLunaDiagnosticsOffset),
                    ReservedHookDiagnostics = ReadUInt32(view, ReservedHookDiagnosticsOffset),
                    LoopbackRingOffset = ReadUInt32(view, LoopbackRingOffset),
                    LoopbackRingCapacity = ReadUInt32(view, LoopbackRingCapacityOffset),
                    LoopbackSampleRate = ReadUInt32(view, LoopbackSampleRateOffset),
                    LoopbackChannels = ReadUInt32(view, LoopbackChannelsOffset),
                    LoopbackBitsPerSample = ReadUInt32(view, LoopbackBitsPerSampleOffset),
                    LoopbackDiagnostics = ReadUInt32(view, LoopbackDiagnosticsOffset),
                    LoopbackTotalWritten = ReadUInt64(view, LoopbackTotalWrittenOffset),
                    LoopbackMarkerCount = ReadUInt64(view, LoopbackMarkerCountOffset),
                    TextLaneCount = ReadUInt32(view, TextLaneCountOffset),
                    TextLaneSlotCount = ReadUInt32(view, TextLaneSlotCountOffset),
                    TextLaneRecycleCount = ReadUInt64(view, TextLaneRecycleCountOffset),
                    TextLaneOverflowCount = ReadUInt64(view, TextLaneOverflowCountOffset),
                    LookupRegionOffset = ReadUInt32(view, LookupRegionOffset),
                    LookupBitmapBytes = ReadUInt32(view, LookupBitmapBytesOffset),
                    LookupFrameCount = ReadUInt32(view, LookupFrameCountOffset),
                    LookupInputSlotCount = ReadUInt32(view, LookupInputSlotCountOffset),
                    LookupHitCount = ReadUInt64(view, LookupHitCountOffset),
                    LookupFrameCountWritten = ReadUInt64(view, LookupFrameCountWrittenOffset),
                    LookupInputCount = ReadUInt64(view, LookupInputCountOffset),
                    LookupEnabled = ReadUInt32(view, LookupEnabledOffset),
                    LookupDiagnostics = ReadUInt32(view, LookupDiagnosticsOffset),
                    LookupFrameAppliedSequence = ReadUInt64(view, LookupFrameAppliedSequenceOffset),
                };
            }
            finally
            {
                UnmapViewOfFile(view);
            }
        }
        finally
        {
            CloseHandle(mapping);
        }
    }

    public IReadOnlyList<GalGameTextLine> TryPollText(
        int processId,
        ulong afterSequence,
        out ulong textWriteCount)
    {
        ulong observedCount = 0;
        var lines = WithView(processId, view =>
        {
            if (!HasCompatibleHeader(view))
                return [];

            var count = ReadUInt64(view, TextWriteCountOffset);
            observedCount = count;
            var laneCount = Math.Clamp((int)ReadUInt32(view, TextLaneCountOffset), 0, TextLaneCount);
            var slotCount = Math.Clamp((int)ReadUInt32(view, TextLaneSlotCountOffset), 0, TextLaneSlotCount);
            // The native writer deliberately keeps every hook lane alive so the
            // workbench can show candidates. Once the user picks a lane, the
            // host must apply the same exact-thread filter as Fushi; otherwise
            // the overlay appears to ignore the selected hook and mixes lines
            // from unrelated TextRender/Luna/native producers.
            var selectedThreadId = ReadUInt64(view, SelectedTextThreadIdOffset);
            var textRegion = ReadUInt32(view, TextRegionOffset);
            if (laneCount == 0 || slotCount == 0 || textRegion == 0 || count <= afterSequence)
                return [];

            var lines = new List<GalGameTextLine>();
            for (var lane = 0; lane < laneCount; lane++)
            {
                var laneBase = IntPtr.Add(view, checked((int)textRegion + lane * TextLaneSize));
                var threadId = ReadUInt64(laneBase, 0);
                var written = ReadUInt64(laneBase, 8);
                if (threadId == 0 || written == 0)
                    continue;

                var first = written > (ulong)slotCount ? written - (ulong)slotCount + 1 : 1;
                for (var laneSequence = first; laneSequence <= written; laneSequence++)
                {
                    var slotIndex = (laneSequence - 1) % (ulong)slotCount;
                    var slotOffset = checked((int)textRegion
                        + laneCount * TextLaneSize
                        + (int)(((ulong)lane * (ulong)slotCount + slotIndex) * TextSlotBytes));
                    var slot = IntPtr.Add(view, slotOffset);
                    if (ReadUInt64(slot, 88) != laneSequence)
                        continue;

                    var sequence = ReadUInt64(slot, 0);
                    if (sequence <= afterSequence || sequence > count)
                        continue;

                    var lineThreadId = ReadUInt64(slot, 24);
                    var eventKind = ReadUInt32(slot, 72);
                    // ThreadCreate is the authoritative Luna candidate directory.
                    // It deliberately carries no text and must stay visible even
                    // after the user selects a different dialogue lane.
                    if (eventKind == 0
                        && selectedThreadId != 0
                        && lineThreadId != selectedThreadId)
                        continue;

                    var byteLength = Math.Min(ReadUInt32(slot, 16), (uint)(TextSlotBytes - TextPayloadOffset));
                    var isUtf8 = ReadUInt32(slot, 20) != 0;
                    var text = ReadText(slot, TextPayloadOffset, byteLength, isUtf8);
                    if (eventKind == 0 && string.IsNullOrWhiteSpace(text))
                        continue;

                    lines.Add(new GalGameTextLine
                    {
                        ProcessId = processId,
                        Sequence = sequence,
                        TimestampMs = ReadUInt64(slot, 8),
                        ThreadId = lineThreadId,
                        FaceId = ReadUInt64(slot, 80),
                        SourceKind = ReadUInt32(slot, 60),
                        EventKind = eventKind,
                        Text = text.Trim(),
                        HookName = ReadAnsi(slot, TextHookNameOffset, Math.Min(ReadUInt32(slot, 64), 64)),
                        HookCode = ReadUtf16(slot, TextHookCodeOffset, Math.Min(ReadUInt32(slot, 68), 128)),
                    });
                }
            }

            return lines
                .OrderBy(line => line.Sequence)
                .Take(TextLaneCount * TextLaneSlotCount)
                .ToArray();
        }) ?? [];
        textWriteCount = observedCount;
        return lines;
    }

    public IReadOnlyList<GalGameThreadPreview> TryReadThreadPreviews(int processId)
    {
        return WithView(processId, view =>
        {
            if (!HasCompatibleHeader(view))
                return [];
            var offset = ReadUInt32(view, 21176);
            var count = Math.Clamp((int)ReadUInt32(view, 21180), 0, TextLaneCount);
            if (offset == 0 || count == 0)
                return [];

            var previews = new List<GalGameThreadPreview>();
            for (var i = 0; i < count; i++)
            {
                var slot = IntPtr.Add(view, checked((int)offset + i * ThreadPreviewSlotSize));
                var sequence = ReadUInt64(slot, 0);
                if (sequence == 0 || (sequence & 1) != 0)
                    continue;
                var threadId = ReadUInt64(slot, 8);
                var endSequence = ReadUInt64(slot, 0);
                if (threadId == 0 || endSequence != sequence)
                    continue;
                var byteLength = Math.Min(ReadUInt32(slot, 40), (uint)(ThreadPreviewTextChars * 2));
                previews.Add(new GalGameThreadPreview
                {
                    ThreadId = threadId,
                    Sequence = sequence,
                    TimestampMs = ReadUInt64(slot, 16),
                    LineCount = ReadUInt64(slot, 24),
                    ArtifactCount = ReadUInt64(slot, 32),
                    EventFlags = ReadUInt32(slot, 44),
                    Text = ReadUtf16(slot, ThreadPreviewTextOffset, byteLength / 2),
                });
            }
            return previews.OrderByDescending(item => item.TimestampMs).ToArray();
        }) ?? [];
    }

    public bool TrySelectTextThread(int processId, ulong threadId)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
            return false;
        var mappingName = $"Local\\FushiVoiceHook_{processId}";
        var mapping = OpenFileMapping(FileMapRead | FileMapWrite, false, mappingName);
        if (mapping == IntPtr.Zero)
            return false;
        try
        {
            var view = MapViewOfFile(mapping, FileMapRead | FileMapWrite, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero || !HasCompatibleHeader(view))
                return false;
            try
            {
                Marshal.WriteInt64(view, SelectedTextThreadIdOffset, unchecked((long)threadId));
                return true;
            }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(mapping); }
    }

    public GalGameLookupHit? TryReadLookupHit(
        int processId,
        ulong afterSequence,
        out ulong hitCount)
    {
        ulong observedCount = 0;
        var hit = WithView(processId, view =>
        {
            if (!HasCompatibleHeader(view) || !HasLookupRegion(view))
                return null;

            observedCount = ReadUInt64(view, LookupHitCountOffset);
            var region = ReadUInt32(view, LookupRegionOffset);
            var sequence = ReadUInt64(view, checked((int)region));
            if (sequence == 0 || sequence <= afterSequence)
                return null;

            var lineBytes = Math.Min(
                ReadUInt32(view, checked((int)region + 44)),
                (uint)(GalGameLookupHitLineBytes));
            var line = ReadText(
                view,
                checked((int)region + LookupHitLineOffset),
                lineBytes,
                isUtf8: true);
            var result = new GalGameLookupHit
            {
                ProcessId = processId,
                Sequence = sequence,
                CharacterIndex = ReadUInt32(view, checked((int)region + 8)),
                CharacterCount = ReadUInt32(view, checked((int)region + 12)),
                GlyphX = ReadInt32(view, checked((int)region + 16)),
                GlyphY = ReadInt32(view, checked((int)region + 20)),
                GlyphWidth = ReadInt32(view, checked((int)region + 24)),
                GlyphHeight = ReadInt32(view, checked((int)region + 28)),
                ViewWidth = ReadInt32(view, checked((int)region + 32)),
                ViewHeight = ReadInt32(view, checked((int)region + 36)),
                Line = line,
            };

            // seq is the writer's release marker. Never consume a partially
            // updated hit if the hook published a newer one while copying.
            return ReadUInt64(view, checked((int)region)) == sequence
                ? result
                : null;
        });
        hitCount = observedCount;
        return hit;
    }

    public IReadOnlyList<GalGameLookupInput> TryReadLookupInputs(
        int processId,
        ulong afterSequence,
        out ulong inputCount)
    {
        ulong observedCount = 0;
        var inputs = WithView(processId, view =>
        {
            if (!HasCompatibleHeader(view) || !HasLookupRegion(view))
                return [];

            observedCount = ReadUInt64(view, LookupInputCountOffset);
            var region = ReadUInt32(view, LookupRegionOffset);
            var slotCount = Math.Clamp(
                (int)ReadUInt32(view, LookupInputSlotCountOffset),
                0,
                4096);
            if (slotCount == 0 || observedCount <= afterSequence)
                return [];

            var first = Math.Max(
                afterSequence + 1,
                observedCount > (ulong)slotCount
                    ? observedCount - (ulong)slotCount + 1
                    : 1);
            var result = new List<GalGameLookupInput>();
            for (var sequence = first; sequence <= observedCount; sequence++)
            {
                var index = (int)((sequence - 1) % (ulong)slotCount);
                var slot = checked((int)region + LookupInputRegionOffset + index * LookupInputBytes);
                if (ReadUInt64(view, slot) != sequence)
                    continue;

                result.Add(new GalGameLookupInput
                {
                    Sequence = sequence,
                    X = ReadInt32(view, slot + 8),
                    Y = ReadInt32(view, slot + 12),
                    Kind = ReadUInt32(view, slot + 16),
                    Wheel = ReadInt32(view, slot + 20),
                    Keys = ReadUInt32(view, slot + 24),
                });
            }
            return result;
        }) ?? [];
        inputCount = observedCount;
        return inputs;
    }

    public bool TrySetLookupEnabled(int processId, bool enabled)
    {
        return WithWritableView(processId, view =>
        {
            if (!HasCompatibleHeader(view) || !HasLookupRegion(view))
                return false;
            Marshal.WriteInt32(view, LookupEnabledOffset, enabled ? 1 : 0);
            return true;
        });
    }

    public bool TryPublishLookupFrame(int processId, GalGameLookupCardFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return WithWritableView(processId, view =>
        {
            if (!HasCompatibleHeader(view) || !HasLookupRegion(view)
                || frame.Width <= 0 || frame.Height <= 0 || frame.Pitch < frame.Width * 4
                || frame.Bgra.Length != checked(frame.Pitch * frame.Height))
            {
                return false;
            }

            var bitmapBytes = ReadUInt32(view, LookupBitmapBytesOffset);
            var frameCount = ReadUInt32(view, LookupFrameCountOffset);
            if (bitmapBytes == 0 || frameCount < 2 || frame.Bgra.Length > bitmapBytes)
                return false;

            var nextSequence = ReadUInt64(view, LookupFrameCountWrittenOffset) + 1;
            var frameIndex = (uint)(nextSequence % frameCount);
            var region = ReadUInt32(view, LookupRegionOffset);
            var frameOffset = checked((int)region + LookupHitBytes
                + checked((int)ReadUInt32(view, LookupInputSlotCountOffset)) * LookupInputBytes
                + checked((int)frameIndex * LookupFrameBytes));
            var bitmapOffset = checked((int)region + LookupHitBytes
                + checked((int)ReadUInt32(view, LookupInputSlotCountOffset)) * LookupInputBytes
                + checked((int)frameCount * LookupFrameBytes)
                + checked((int)frameIndex * (int)bitmapBytes));

            Marshal.WriteInt32(view, frameOffset + LookupFrameReadyOffset, 0);
            Marshal.WriteInt64(view, frameOffset, unchecked((long)nextSequence));
            Marshal.WriteInt64(view, frameOffset + 8, unchecked((long)frame.HitSequence));
            Marshal.WriteInt32(view, frameOffset + 16, 0);
            Marshal.WriteInt32(view, frameOffset + 20, frame.Width);
            Marshal.WriteInt32(view, frameOffset + 24, frame.Height);
            Marshal.WriteInt32(view, frameOffset + 28, frame.Pitch);
            Marshal.WriteInt32(view, frameOffset + 32, frame.AnchorX);
            Marshal.WriteInt32(view, frameOffset + 36, frame.AnchorY);
            Marshal.WriteInt32(view, frameOffset + 40, frame.HighlightStart);
            Marshal.WriteInt32(view, frameOffset + 44, frame.HighlightLength);
            Marshal.WriteInt32(view, frameOffset + LookupFrameByteLengthOffset, frame.Bgra.Length);
            Marshal.Copy(frame.Bgra, 0, IntPtr.Add(view, bitmapOffset), frame.Bgra.Length);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(view, frameOffset + LookupFrameReadyOffset, 1);
            Thread.MemoryBarrier();
            Marshal.WriteInt64(view, LookupFrameCountWrittenOffset, unchecked((long)nextSequence));
            return true;
        });
    }

    public bool TryPublishLookupDismiss(int processId, ulong hitSequence)
    {
        return WithWritableView(processId, view =>
        {
            if (!HasCompatibleHeader(view) || !HasLookupRegion(view))
                return false;
            var frameCount = ReadUInt32(view, LookupFrameCountOffset);
            var slotCount = ReadUInt32(view, LookupInputSlotCountOffset);
            if (frameCount < 2 || slotCount == 0)
                return false;

            var nextSequence = ReadUInt64(view, LookupFrameCountWrittenOffset) + 1;
            var frameIndex = (uint)(nextSequence % frameCount);
            var region = ReadUInt32(view, LookupRegionOffset);
            var frameOffset = checked((int)region + LookupHitBytes
                + checked((int)slotCount) * LookupInputBytes
                + checked((int)frameIndex * LookupFrameBytes));
            Marshal.WriteInt32(view, frameOffset + LookupFrameReadyOffset, 0);
            Marshal.WriteInt64(view, frameOffset, unchecked((long)nextSequence));
            Marshal.WriteInt64(view, frameOffset + 8, unchecked((long)hitSequence));
            Marshal.WriteInt32(view, frameOffset + 16, 1);
            Marshal.WriteInt32(view, frameOffset + 20, 0);
            Marshal.WriteInt32(view, frameOffset + 24, 0);
            Marshal.WriteInt32(view, frameOffset + 28, 0);
            Marshal.WriteInt32(view, frameOffset + LookupFrameByteLengthOffset, 0);
            Thread.MemoryBarrier();
            Marshal.WriteInt32(view, frameOffset + LookupFrameReadyOffset, 1);
            Thread.MemoryBarrier();
            Marshal.WriteInt64(view, LookupFrameCountWrittenOffset, unchecked((long)nextSequence));
            return true;
        });
    }

    public GalGameAudioCapture? TryGrabClipNear(
        int processId,
        ulong timestampMs,
        ulong toleranceMs = 1800)
    {
        return WithView(processId, view =>
        {
            if (!HasCompatibleHeader(view))
                return null;
            var capacity = ReadUInt32(view, RingCapacityOffset);
            var clipRegion = ReadUInt32(view, ClipRegionOffset);
            var clipCount = ReadUInt64(view, ClipWriteCountOffset);
            if (capacity == 0 || clipRegion == 0 || clipCount == 0)
                return null;

            GalGameAudioCapture? best = null;
            ulong bestDifference = toleranceMs + 1;
            var totalWritten = ReadUInt64(view, TotalWrittenOffset);
            var scanStart = clipCount > 1024 ? clipCount - 1024 : 0;
            for (var sequence = scanStart + 1; sequence <= clipCount; sequence++)
            {
                var slotIndex = (sequence - 1) % 1024;
                var clip = IntPtr.Add(view, checked((int)clipRegion + (int)(slotIndex * VoiceClipSize)));
                if (ReadUInt64(clip, 0) != sequence)
                    continue;
                var length = ReadUInt32(clip, 28);
                if (length == 0 || length > capacity)
                    continue;
                var clipTotal = ReadUInt64(clip, 16);
                if (totalWritten > clipTotal && totalWritten - clipTotal > capacity - length)
                    continue;
                var clipTimestamp = ReadUInt64(clip, 8);
                var difference = clipTimestamp > timestampMs
                    ? clipTimestamp - timestampMs
                    : timestampMs - clipTimestamp;
                if (difference >= bestDifference)
                    continue;

                var ringOffset = ReadUInt32(clip, 24) % capacity;
                var pcm = ReadRing(view, SharedHeaderSize, capacity, ringOffset, length);
                if (pcm.Length == 0)
                    continue;
                bestDifference = difference;
                best = new GalGameAudioCapture
                {
                    Pcm = pcm,
                    SampleRate = (int)ReadUInt32(clip, 32),
                    Channels = (int)ReadUInt32(clip, 36),
                    BitsPerSample = (int)ReadUInt32(clip, 40),
                    IsFloat = ReadUInt32(clip, 44) != 0,
                    TimestampMs = clipTimestamp,
                    SourcePtr = ReadUInt64(clip, VoiceClipSourceOffset),
                };
            }
            return best;
        });
    }

    public GalGameAudioCapture? TryGrabLoopbackWindow(
        int processId,
        ulong timestampMs,
        int preRollMs = 1000,
        int postRollMs = 4000)
    {
        return WithView(processId, view =>
        {
            if (!HasCompatibleHeader(view) || timestampMs == 0)
                return null;

            var ringOffset = ReadUInt32(view, LoopbackRingOffset);
            var capacity = ReadUInt32(view, LoopbackRingCapacityOffset);
            var markerOffset = ReadUInt32(view, LoopbackMarkerOffset);
            var markerSlots = Math.Clamp(
                (int)ReadUInt32(view, LoopbackMarkerSlotCountOffset), 0, 4096);
            var sampleRate = ReadUInt32(view, LoopbackSampleRateOffset);
            var channels = ReadUInt32(view, LoopbackChannelsOffset);
            var bits = ReadUInt32(view, LoopbackBitsPerSampleOffset);
            var currentTotal = ReadUInt64(view, LoopbackTotalWrittenOffset);
            var markerCount = ReadUInt64(view, LoopbackMarkerCountOffset);
            if (ringOffset == 0 || capacity == 0 || markerOffset == 0
                || markerSlots == 0 || sampleRate == 0 || channels == 0
                || bits != 16 || currentTotal == 0 || markerCount == 0)
            {
                return null;
            }

            var markers = new List<(ulong Tick, ulong Total)>();
            var firstSequence = markerCount > (ulong)markerSlots
                ? markerCount - (ulong)markerSlots + 1
                : 1;
            for (var sequence = firstSequence; sequence <= markerCount; sequence++)
            {
                var slotIndex = (sequence - 1) % (ulong)markerSlots;
                var marker = IntPtr.Add(
                    view,
                    checked((int)markerOffset + (int)slotIndex * LoopbackMarkerSize));
                var begin = ReadUInt64(marker, 0);
                if (begin != sequence)
                    continue;
                var tick = ReadUInt64(marker, 8);
                var total = ReadUInt64(marker, 16);
                if (ReadUInt64(marker, 0) == begin && tick > 0)
                    markers.Add((tick, total));
            }
            if (markers.Count == 0)
                return null;

            markers.Sort(static (left, right) => left.Tick.CompareTo(right.Tick));
            var bytesPerSecond = checked((ulong)sampleRate * channels * 2u);
            var preRoll = (ulong)Math.Max(0, preRollMs);
            var startTick = timestampMs > preRoll ? timestampMs - preRoll : 0;
            var endTick = timestampMs + (ulong)Math.Max(0, postRollMs);
            var startTotal = TickToLoopbackTotal(markers, startTick, bytesPerSecond, currentTotal);
            var endTotal = TickToLoopbackTotal(markers, endTick, bytesPerSecond, currentTotal);
            var floor = currentTotal > capacity ? currentTotal - capacity : 0;
            startTotal = Math.Max(startTotal, floor);
            endTotal = Math.Min(endTotal, currentTotal);

            var blockAlign = checked((ulong)channels * 2u);
            var remainder = startTotal % blockAlign;
            if (remainder != 0)
                startTotal += blockAlign - remainder;
            endTotal -= endTotal % blockAlign;
            if (endTotal <= startTotal)
                return null;

            var length = Math.Min(endTotal - startTotal, capacity);
            length -= length % blockAlign;
            if (length == 0 || length > int.MaxValue)
                return null;

            var pcm = new byte[(int)length];
            var readOffset = (uint)(startTotal % capacity);
            var firstBytes = Math.Min(pcm.Length, checked((int)(capacity - readOffset)));
            Marshal.Copy(
                IntPtr.Add(view, checked((int)ringOffset + (int)readOffset)),
                pcm,
                0,
                firstBytes);
            if (pcm.Length > firstBytes)
            {
                Marshal.Copy(
                    IntPtr.Add(view, checked((int)ringOffset)),
                    pcm,
                    firstBytes,
                    pcm.Length - firstBytes);
            }

            var sampleCount = pcm.Length / 2;
            var peak = 0;
            for (var index = 0; index < sampleCount; index++)
            {
                var sample = (short)(pcm[index * 2] | (pcm[index * 2 + 1] << 8));
                peak = Math.Max(peak, Math.Abs((int)sample));
            }
            if (peak == 0)
                return null;

            var threshold = Math.Max(1, (int)(peak * 0.08));
            var lastSample = sampleCount;
            while (lastSample > 0)
            {
                var offset = (lastSample - 1) * 2;
                var sample = (short)(pcm[offset] | (pcm[offset + 1] << 8));
                if (Math.Abs((int)sample) >= threshold)
                    break;
                lastSample--;
            }
            lastSample -= lastSample % (int)channels;
            if (lastSample == 0)
                return null;
            if (lastSample * 2 < pcm.Length)
                Array.Resize(ref pcm, lastSample * 2);

            return new GalGameAudioCapture
            {
                Pcm = pcm,
                SampleRate = (int)sampleRate,
                Channels = (int)channels,
                BitsPerSample = 16,
                IsFloat = false,
                TimestampMs = timestampMs,
            };
        });
    }

    public GalGameAudioCapture? TryGrabRecent(int processId, int backMs = 1500)
    {
        return WithView(processId, view =>
        {
            if (!HasCompatibleHeader(view) || backMs <= 0)
                return null;
            var sampleRate = ReadUInt32(view, SampleRateOffset);
            var channels = ReadUInt32(view, ChannelsOffset);
            var bits = ReadUInt32(view, BitsPerSampleOffset);
            var capacity = ReadUInt32(view, RingCapacityOffset);
            var blockAlign = ReadUInt32(view, BlockAlignOffset);
            var totalWritten = ReadUInt64(view, TotalWrittenOffset);
            var writePos = ReadUInt32(view, WritePosOffset);
            if (sampleRate == 0 || channels == 0 || bits == 0 || capacity == 0 || blockAlign == 0 || writePos > capacity)
                return null;
            var filled = Math.Min((ulong)capacity, totalWritten);
            var wanted = Math.Min(filled, (ulong)sampleRate * blockAlign * (ulong)backMs / 1000);
            wanted -= wanted % blockAlign;
            if (wanted == 0)
                return null;
            var start = (uint)(((ulong)writePos + capacity - wanted % capacity) % capacity);
            return new GalGameAudioCapture
            {
                Pcm = ReadRing(view, SharedHeaderSize, capacity, start, (uint)wanted),
                SampleRate = (int)sampleRate,
                Channels = (int)channels,
                BitsPerSample = (int)bits,
                IsFloat = ReadUInt32(view, IsFloatOffset) != 0,
                TimestampMs = 0,
            };
        });
    }

    private static ulong TickToLoopbackTotal(
        IReadOnlyList<(ulong Tick, ulong Total)> markers,
        ulong tick,
        ulong bytesPerSecond,
        ulong currentTotal)
    {
        var first = markers[0];
        var last = markers[^1];
        if (tick <= first.Tick)
        {
            var back = (first.Tick - tick) * bytesPerSecond / 1000;
            return back >= first.Total ? 0 : first.Total - back;
        }
        if (tick >= last.Tick)
        {
            var forward = (tick - last.Tick) * bytesPerSecond / 1000;
            var projected = last.Total > ulong.MaxValue - forward
                ? ulong.MaxValue
                : last.Total + forward;
            return Math.Min(projected, currentTotal);
        }

        for (var index = 1; index < markers.Count; index++)
        {
            var right = markers[index];
            if (tick > right.Tick)
                continue;
            var left = markers[index - 1];
            var tickSpan = Math.Max(1ul, right.Tick - left.Tick);
            var totalSpan = right.Total > left.Total ? right.Total - left.Total : 0;
            return left.Total + totalSpan * (tick - left.Tick) / tickSpan;
        }
        return last.Total;
    }

    private static T? WithView<T>(int processId, Func<IntPtr, T?> reader)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
            return default;
        var mappingName = $"Local\\FushiVoiceHook_{processId}";
        var mapping = OpenFileMapping(FileMapRead, false, mappingName);
        if (mapping == IntPtr.Zero)
            return default;
        try
        {
            var view = MapViewOfFile(mapping, FileMapRead, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
                return default;
            try { return reader(view); }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(mapping); }
    }

    private static T WithWritableView<T>(int processId, Func<IntPtr, T> writer)
    {
        if (!OperatingSystem.IsWindows() || processId <= 0)
            return default!;
        var mappingName = $"Local\\FushiVoiceHook_{processId}";
        var mapping = OpenFileMapping(FileMapRead | FileMapWrite, false, mappingName);
        if (mapping == IntPtr.Zero)
            return default!;
        try
        {
            var view = MapViewOfFile(mapping, FileMapRead | FileMapWrite, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
                return default!;
            try { return writer(view); }
            finally { UnmapViewOfFile(view); }
        }
        finally { CloseHandle(mapping); }
    }

    private static bool HasCompatibleHeader(IntPtr view) =>
        ReadUInt32(view, MagicOffset) == GalGameIpcSnapshot.SharedMagic
        && ReadUInt32(view, VersionOffset) == GalGameIpcSnapshot.SharedVersion
        && ReadUInt32(view, IpcProtocolVersionOffset) == GalGameIpcSnapshot.StableIpcVersion;

    private static bool HasLookupRegion(IntPtr view) =>
        ReadUInt32(view, LookupRegionOffset) != 0
        && ReadUInt32(view, LookupBitmapBytesOffset) != 0
        && ReadUInt32(view, LookupFrameCountOffset) >= 2
        && ReadUInt32(view, LookupInputSlotCountOffset) != 0;

    private static byte[] ReadRing(IntPtr view, uint ringBase, uint capacity, uint offset, uint length)
    {
        if (capacity == 0 || length == 0 || length > capacity || offset >= capacity)
            return [];
        var result = new byte[length];
        var first = Math.Min(length, capacity - offset);
        Marshal.Copy(IntPtr.Add(view, checked((int)(ringBase + offset))), result, 0, checked((int)first));
        if (length > first)
            Marshal.Copy(IntPtr.Add(view, checked((int)ringBase)), result, checked((int)first), checked((int)(length - first)));
        return result;
    }

    private static string ReadText(IntPtr slot, int offset, uint byteLength, bool isUtf8)
    {
        if (byteLength == 0)
            return string.Empty;
        var bytes = new byte[byteLength];
        Marshal.Copy(IntPtr.Add(slot, offset), bytes, 0, bytes.Length);
        return isUtf8
            ? Encoding.UTF8.GetString(bytes)
            : Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static string ReadAnsi(IntPtr view, int offset, uint length)
    {
        if (length == 0)
            return string.Empty;
        var bytes = new byte[length];
        Marshal.Copy(IntPtr.Add(view, offset), bytes, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }

    private static string ReadUtf16(IntPtr view, int offset, uint charCount)
    {
        if (charCount == 0)
            return string.Empty;
        var bytes = new byte[checked((int)charCount * 2)];
        Marshal.Copy(IntPtr.Add(view, offset), bytes, 0, bytes.Length);
        return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
    }

    private static uint ReadUInt32(IntPtr view, int offset) =>
        unchecked((uint)Marshal.ReadInt32(view, offset));

    private static int ReadInt32(IntPtr view, int offset) =>
        Marshal.ReadInt32(view, offset);

    private static ulong ReadUInt64(IntPtr view, int offset) =>
        unchecked((ulong)Marshal.ReadInt64(view, offset));

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenFileMapping(uint desiredAccess, bool inheritHandle, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr MapViewOfFile(
        IntPtr mapping,
        uint desiredAccess,
        uint fileOffsetHigh,
        uint fileOffsetLow,
        UIntPtr numberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(IntPtr baseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
