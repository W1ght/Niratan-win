using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Video;

internal sealed class RemoteVideoPlaybackSession
{
    private readonly IRemoteVideoResolver _resolver;
    private long _generation;

    public RemoteVideoPlaybackSession(IRemoteVideoResolver resolver)
    {
        _resolver = resolver;
    }

    public ResolvedRemoteVideoSource? Source { get; private set; }
    public string? SubtitlePath { get; private set; }
    public string? SubtitleLanguage { get; private set; }

    public async Task<ResolvedRemoteVideoSource> InitializeAsync(
        VideoItem video,
        ResolvedRemoteVideoSource? source,
        CancellationToken ct)
    {
        var generation = Interlocked.Increment(ref _generation);
        source ??= await _resolver.ResolveAsync(
            video.GetRemoteIdentity() ?? throw new InvalidOperationException("Remote video identity is incomplete."),
            video.RemoteSubtitleLanguage,
            forceRefresh: false,
            ct);
        ThrowIfStale(generation, ct);
        Source = source;
        return source;
    }

    public async Task<ResolvedRemoteVideoSource> RefreshAsync(int? preferredHeight, CancellationToken ct)
    {
        var current = Source ?? throw new InvalidOperationException("Remote video is not resolved.");
        var generation = Interlocked.Increment(ref _generation);
        var refreshed = await _resolver.ResolveAsync(
            current.Identity,
            current.SelectedSubtitleLanguage,
            forceRefresh: true,
            ct);
        ThrowIfStale(generation, ct);

        if (preferredHeight.HasValue)
        {
            var quality = FindClosestQuality(refreshed.QualityOptions, preferredHeight.Value);
            refreshed = refreshed.SelectQuality(quality.Id) ?? refreshed;
        }

        Source = refreshed;
        return refreshed;
    }

    public bool SelectQuality(string qualityId)
    {
        var selected = Source?.SelectQuality(qualityId);
        if (selected == null)
            return false;

        Interlocked.Increment(ref _generation);
        Source = selected;
        return true;
    }

    public bool SelectMuxedFallback()
    {
        var source = Source;
        if (source?.MuxedFallbackStream == null)
            return false;

        Interlocked.Increment(ref _generation);
        Source = source with
        {
            PlaybackStream = source.MuxedFallbackStream,
            AudioStream = null,
        };
        return true;
    }

    public async Task<string?> DownloadPreferredSubtitleAsync(
        string? savedLanguage,
        string outputDirectory,
        CancellationToken ct)
    {
        var source = Source ?? throw new InvalidOperationException("Remote video is not resolved.");
        var option = source.PreferredSubtitle([savedLanguage]);
        if (option == null)
            return null;

        return await DownloadSubtitleAsync(option, outputDirectory, ct);
    }

    public async Task<string> DownloadSubtitleAsync(
        RemoteVideoSubtitleOption option,
        string outputDirectory,
        CancellationToken ct)
    {
        var source = Source ?? throw new InvalidOperationException("Remote video is not resolved.");
        var generation = Interlocked.Increment(ref _generation);
        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(
            outputDirectory,
            $"{source.Identity.RemoteId}-{SanitizeLanguage(option.Language)}.srt");
        var downloaded = await _resolver.DownloadSubtitleAsync(option, path, ct);
        ThrowIfStale(generation, ct);
        SubtitlePath = downloaded;
        SubtitleLanguage = option.Language;
        return downloaded;
    }

    public VideoPlaybackRequest CreatePlaybackRequest(TimeSpan? startPosition)
    {
        var source = Source ?? throw new InvalidOperationException("Remote video is not resolved.");
        var headers = MergeHeaders(source.PlaybackStream.HttpHeaders, source.AudioStream?.HttpHeaders);
        return new VideoPlaybackRequest(
            source.PlaybackStream.Url,
            source.AudioStream?.Url,
            null,
            headers,
            startPosition);
    }

    public void Invalidate() => Interlocked.Increment(ref _generation);

    private void ThrowIfStale(long generation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (generation != Interlocked.Read(ref _generation))
            throw new OperationCanceledException("A newer remote-video operation replaced this one.", ct);
    }

    private static RemoteVideoQualityOption FindClosestQuality(
        IReadOnlyList<RemoteVideoQualityOption> options,
        int preferredHeight)
    {
        if (options.Count == 0)
            throw new InvalidOperationException("No playable quality is available.");

        var best = options[0];
        var bestDistance = Math.Abs(best.Height - preferredHeight);
        for (var index = 1; index < options.Count; index++)
        {
            var candidate = options[index];
            var distance = Math.Abs(candidate.Height - preferredHeight);
            if (distance < bestDistance || (distance == bestDistance && candidate.Height > best.Height))
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static IReadOnlyDictionary<string, string> MergeHeaders(
        IReadOnlyDictionary<string, string> primary,
        IReadOnlyDictionary<string, string>? secondary)
    {
        var result = new Dictionary<string, string>(primary, StringComparer.OrdinalIgnoreCase);
        if (secondary != null)
        {
            foreach (var pair in secondary)
                result.TryAdd(pair.Key, pair.Value);
        }

        return result;
    }

    private static string SanitizeLanguage(string language)
    {
        var chars = language.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (!char.IsLetterOrDigit(chars[index]) && chars[index] is not '-' and not '_')
                chars[index] = '_';
        }

        return new string(chars);
    }
}
