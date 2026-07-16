using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Niratan.Helpers;
using Niratan.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Niratan.ViewModels.Components;

public sealed class VideoItemViewModel
{
    public VideoItemViewModel(VideoItem video)
    {
        Video = video;
    }

    public VideoItem Video { get; }

    public string AutomationId => $"VideoItem_{Video.Id}";

    public string SubtitleStatus => string.IsNullOrWhiteSpace(Video.SubtitlePath)
                                    && string.IsNullOrWhiteSpace(Video.RemoteSubtitleLanguage)
        ? ResourceStringHelper.GetString("VideoSubtitleStatusNone", "No subtitles")
        : ResourceStringHelper.GetString("VideoSubtitleStatusAvailable", "Subtitles");

    public double OverallProgress => !HasMeaningfulProgress || Video.DurationSeconds <= 0
        ? 0
        : Math.Clamp(Video.LastPositionSeconds / Video.DurationSeconds, 0, 1);

    public double OverallProgressPercent => OverallProgress * 100;

    public string ProgressText => Video.IsWatched
        ? ResourceStringHelper.GetString("VideoWatchStatusWatched", "Watched")
        : !HasMeaningfulProgress || Video.DurationSeconds <= 0
            ? ""
            : $"{OverallProgress:P0}";

    public string WatchStatusText => Video.IsWatched
        ? ResourceStringHelper.GetString("VideoWatchStatusWatched", "Watched")
        : HasMeaningfulProgress
            ? ResourceStringHelper.GetString("VideoWatchStatusContinue", "Continue")
            : ResourceStringHelper.GetString("VideoWatchStatusUnwatched", "Unwatched");

    public string FolderName => Video.IsRemote
        ? ResourceStringHelper.GetString("YouTubeVideo", "YouTube Video")
        : string.IsNullOrWhiteSpace(Video.SourceFolderPath)
            ? ""
            : Path.GetFileName(Video.SourceFolderPath);

    public IReadOnlyList<string> TagLabels => SplitTags(Video.Tags);

    public string? ArtworkPath => ExistingPath(Video.PosterPath) ?? ExistingPath(Video.ThumbnailPath);

    public BitmapImage? ArtworkImage => LoadPoster(ArtworkPath, Video.RemoteThumbnailUrl);

    public bool HasArtwork => ArtworkImage != null;

    public string FileSizeText => Video.FileSizeBytes <= 0 ? "" : FormatByteCount(Video.FileSizeBytes);

    public string ModifiedDateText => Video.ModifiedAt?.ToLocalTime().ToString("d") ?? "";

    public string ListMetadataText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(FolderName))
                parts.Add(FolderName);
            if (!string.IsNullOrWhiteSpace(FileSizeText))
                parts.Add(FileSizeText);
            if (!string.IsNullOrWhiteSpace(ModifiedDateText))
                parts.Add(ModifiedDateText);

            return string.Join(" • ", parts);
        }
    }

    public string RemainingText => Video.IsWatched || Video.DurationSeconds <= 0
        ? WatchStatusText
        : FormatRemaining();

    public BitmapImage? PosterImage => ArtworkImage;

    public bool HasPoster => HasArtwork;

    private bool HasMeaningfulProgress =>
        Video.LastPositionSeconds >= VideoPlaybackState.MinimumPersistablePositionSeconds;

    private string FormatRemaining()
    {
        var remainingSeconds = Math.Max(Video.DurationSeconds - Video.LastPositionSeconds, 0);
        return FormatDuration(remainingSeconds);
    }

    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return string.Format(CultureInfo.CurrentCulture, "{0:0.#} {1}", size, units[unitIndex]);
    }

    private static string FormatDuration(double totalSeconds)
    {
        var roundedSeconds = Math.Max(0, (int)Math.Round(totalSeconds, MidpointRounding.AwayFromZero));
        var hours = roundedSeconds / 3600;
        var minutes = (roundedSeconds % 3600) / 60;
        var seconds = roundedSeconds % 60;

        if (hours > 0)
            return $"{hours}h {minutes}m";

        if (minutes > 0)
            return $"{minutes}m";

        return $"{seconds}s";
    }

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private static BitmapImage? LoadPoster(string? posterPath, string? remoteThumbnailUrl)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(posterPath) && File.Exists(posterPath))
                return new BitmapImage(new Uri(posterPath));
            if (Uri.TryCreate(remoteThumbnailUrl, UriKind.Absolute, out var remoteUri)
                && remoteUri.Scheme == Uri.UriSchemeHttps)
                return new BitmapImage(remoteUri);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExistingPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? path
            : null;
}
