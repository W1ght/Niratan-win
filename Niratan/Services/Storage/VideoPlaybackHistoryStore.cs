using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Novels;

namespace Niratan.Services.Storage;

internal sealed class VideoPlaybackHistoryStore : IVideoPlaybackHistoryStore
{
    private readonly INiratanJsonFileStore _json;
    private readonly string _historyPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private VideoPlaybackHistoryDocument? _history;

    public VideoPlaybackHistoryStore(INiratanJsonFileStore json)
        : this(Path.Combine(AppDataHelper.GetDataPath(), "video_playback_history.json"), json)
    {
    }

    internal VideoPlaybackHistoryStore(string historyPath, INiratanJsonFileStore? json = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPath);
        _historyPath = Path.GetFullPath(historyPath);
        _json = json ?? new NiratanJsonFileStore();
    }

    public async Task<VideoPlaybackHistoryEntry> GetAsync(
        string identityKey,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentity(identityKey);
            var document = _history!.PlaybackStates.GetValueOrDefault(key);
            return new VideoPlaybackHistoryEntry(
                CreatePlaybackState(key),
                document?.UpdatedAt,
                document?.IsFinished == true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveProgressAsync(
        string identityKey,
        double positionSeconds,
        double durationSeconds,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentity(identityKey);
            var current = CreatePlaybackState(key);
            SaveCore(key, current with
            {
                PositionSeconds = positionSeconds,
                DurationSeconds = durationSeconds,
            });
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        string identityKey,
        VideoPlaybackState state,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            SaveCore(NormalizeIdentity(identityKey), state);
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateLastOpenedAsync(
        string identityKey,
        DateTimeOffset openedAt,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentity(identityKey);
            if (_history!.PlaybackStates.TryGetValue(key, out var state))
            {
                state.UpdatedAt = openedAt.ToUniversalTime();
                await SaveAsync(ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkWatchedAsync(
        string identityKey,
        DateTimeOffset watchedAt,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentity(identityKey);
            var duration = _history!.PlaybackStates.GetValueOrDefault(key)?.Duration;
            _history.Positions.Remove(key);
            _history.PlaybackStates[key] = new VideoPlaybackStateDocument
            {
                Position = Math.Max(duration ?? 0, 0),
                Duration = duration,
                UpdatedAt = watchedAt.ToUniversalTime(),
                IsFinished = true,
                ResumeOptions = new VideoPlaybackResumeOptionsDocument(),
            };
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearProgressAsync(string identityKey, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            var key = NormalizeIdentity(identityKey);
            _history!.Positions.Remove(key);
            _history.PlaybackStates.Remove(key);
            await SaveAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_history != null)
            return;
        var result = await _json.ReadAsync<VideoPlaybackHistoryDocument>(_historyPath, ct);
        if (result.Status == NovelJsonReadStatus.Invalid)
        {
            throw new InvalidDataException(
                $"Video playback history is invalid and was preserved: {result.Error}");
        }
        _history = result.Value ?? new VideoPlaybackHistoryDocument();
        _history.Positions ??= [];
        _history.PlaybackStates ??= [];
        _history.SubtitleSelections ??= [];
    }

    private void SaveCore(string key, VideoPlaybackState state)
    {
        var position = NormalizeSeconds(state.PositionSeconds);
        var duration = NormalizeSeconds(state.DurationSeconds);
        SaveSubtitleSelection(key, state.SubtitleSelection);
        if (duration <= 0 || position < VideoPlaybackState.MinimumPersistablePositionSeconds)
        {
            _history!.Positions.Remove(key);
            _history.PlaybackStates.Remove(key);
            return;
        }
        if (position >= duration - 5)
        {
            _history!.Positions.Remove(key);
            _history.PlaybackStates[key] = new VideoPlaybackStateDocument
            {
                Position = duration,
                Duration = duration,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsFinished = true,
                ResumeOptions = new VideoPlaybackResumeOptionsDocument(),
            };
            return;
        }
        _history!.Positions[key] = position;
        _history.PlaybackStates[key] = new VideoPlaybackStateDocument
        {
            Position = position,
            Duration = duration,
            UpdatedAt = DateTimeOffset.UtcNow,
            IsFinished = false,
            ResumeOptions = ToResumeOptionsDocument(state),
        };
    }

    private VideoPlaybackState CreatePlaybackState(string key)
    {
        var document = _history!.PlaybackStates.GetValueOrDefault(key);
        var selection = FromSubtitleSelectionDocument(_history.SubtitleSelections.GetValueOrDefault(key));
        var resume = document?.ResumeOptions ?? new VideoPlaybackResumeOptionsDocument();
        return new VideoPlaybackState(
            document?.Position ?? _history.Positions.GetValueOrDefault(key),
            document?.Duration ?? 0,
            selection,
            (int)Math.Round((resume.SubtitleDelay ?? 0) * 1000),
            resume.Speed ?? 1,
            resume.AudioDelay ?? 0,
            FromAudioSelectionDocument(resume.AudioSelection));
    }

    private void SaveSubtitleSelection(string key, VideoSubtitleSelection selection)
    {
        var document = ToSubtitleSelectionDocument(selection);
        if (document == null)
            _history!.SubtitleSelections.Remove(key);
        else
            _history!.SubtitleSelections[key] = document;
    }

    private static VideoPlaybackResumeOptionsDocument ToResumeOptionsDocument(VideoPlaybackState state) =>
        new()
        {
            Speed = Math.Abs(state.PlaybackSpeed - 1) >= 0.001
                ? VideoPlaybackState.NormalizePlaybackSpeed(state.PlaybackSpeed) : null,
            SubtitleDelay = Math.Abs(state.SubtitleDelayMilliseconds) >= 5
                ? VideoPlaybackState.NormalizeSubtitleDelayMilliseconds(state.SubtitleDelayMilliseconds) / 1000d : null,
            AudioDelay = Math.Abs(state.AudioDelaySeconds) >= 0.005
                ? VideoPlaybackState.NormalizeAudioDelaySeconds(state.AudioDelaySeconds) : null,
            AudioSelection = ToAudioSelectionDocument(state.AudioSelection ?? VideoAudioSelection.None()),
        };

    private static VideoSubtitleSelectionDocument? ToSubtitleSelectionDocument(VideoSubtitleSelection selection) =>
        selection.Kind switch
        {
            VideoSubtitleSelectionKind.Off => new VideoSubtitleSelectionDocument { Off = new EmptyVideoSelectionDocument() },
            VideoSubtitleSelectionKind.ExternalFile when !string.IsNullOrWhiteSpace(selection.ExternalPath) =>
                new VideoSubtitleSelectionDocument
                {
                    External = new ExternalVideoSubtitleSelectionDocument { Path = Path.GetFullPath(selection.ExternalPath) },
                },
            VideoSubtitleSelectionKind.EmbeddedTrack when selection.TrackId.HasValue =>
                new VideoSubtitleSelectionDocument
                {
                    Embedded = new EmbeddedVideoSubtitleSelectionDocument
                    {
                        Value = new VideoSubtitleTrackIdentityDocument
                        {
                            TrackID = selection.TrackId.Value,
                            Title = selection.TrackName ?? string.Empty,
                        },
                    },
                },
            VideoSubtitleSelectionKind.RemoteLanguage when !string.IsNullOrWhiteSpace(selection.RemoteLanguageCode) =>
                new VideoSubtitleSelectionDocument
                {
                    Remote = new RemoteLanguageVideoSubtitleSelectionDocument { Language = selection.RemoteLanguageCode },
                },
            _ => null,
        };

    private static VideoSubtitleSelection FromSubtitleSelectionDocument(VideoSubtitleSelectionDocument? document)
    {
        if (document?.Off != null)
            return VideoSubtitleSelection.Off();
        if (document?.External != null && !string.IsNullOrWhiteSpace(document.External.Path))
            return VideoSubtitleSelection.ExternalFile(document.External.Path);
        if (document?.Embedded?.Value != null)
            return VideoSubtitleSelection.EmbeddedTrack(document.Embedded.Value.TrackID, document.Embedded.Value.Title);
        if (document?.Remote != null && !string.IsNullOrWhiteSpace(document.Remote.Language))
            return VideoSubtitleSelection.RemoteLanguage(document.Remote.Language);
        return VideoSubtitleSelection.None();
    }

    private static VideoAudioSelectionDocument? ToAudioSelectionDocument(VideoAudioSelection selection) =>
        selection.Kind switch
        {
            VideoAudioSelectionKind.Off => new VideoAudioSelectionDocument { Off = new EmptyVideoSelectionDocument() },
            VideoAudioSelectionKind.EmbeddedTrack => new VideoAudioSelectionDocument
            {
                Embedded = new EmbeddedVideoAudioSelectionDocument
                {
                    Value = new VideoAudioTrackIdentityDocument
                    {
                        TrackID = selection.TrackId ?? 0,
                        FfIndex = selection.FfIndex,
                        Title = selection.Title ?? string.Empty,
                        Language = selection.Language,
                        Codec = selection.Codec,
                    },
                },
            },
            _ => null,
        };

    private static VideoAudioSelection FromAudioSelectionDocument(VideoAudioSelectionDocument? document)
    {
        if (document?.Off != null)
            return VideoAudioSelection.Off();
        if (document?.Embedded?.Value is { } value)
            return new VideoAudioSelection(
                VideoAudioSelectionKind.EmbeddedTrack,
                value.TrackID,
                value.FfIndex,
                value.Title,
                value.Language,
                value.Codec);
        return VideoAudioSelection.None();
    }

    private Task SaveAsync(CancellationToken ct) => _json.WriteAsync(_historyPath, _history!, ct);

    private static string NormalizeIdentity(string value) => LegacyVideoCatalogReader.NormalizeIdentity(value);
    private static double NormalizeSeconds(double value) => double.IsFinite(value) ? Math.Max(value, 0) : 0;
}
