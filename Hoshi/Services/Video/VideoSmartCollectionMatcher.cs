using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Hoshi.Models;
using Hoshi.Models.Video;

namespace Hoshi.Services.Video;

public static class VideoSmartCollectionMatcher
{
    public static bool Matches(VideoItem video, IReadOnlyList<VideoSmartRule> rules)
    {
        if (rules == null || rules.Count == 0)
            return false;

        return rules.All(rule => MatchesRule(video, rule));
    }

    private static bool MatchesRule(VideoItem video, VideoSmartRule rule) =>
        rule.Field switch
        {
            VideoSmartRuleField.FileName => MatchesText(
                rule,
                video.Title,
                Path.GetFileNameWithoutExtension(video.FilePath)),
            VideoSmartRuleField.ParentFolder => MatchesText(
                rule,
                Path.GetFileName(video.SourceFolderPath ?? string.Empty),
                video.SourceFolderPath ?? string.Empty),
            VideoSmartRuleField.Path => MatchesText(rule, video.FilePath),
            VideoSmartRuleField.Tag => MatchesText(rule, SplitTags(video.Tags).ToArray()),
            VideoSmartRuleField.HasBoundSubtitle => MatchesBool(
                rule,
                !string.IsNullOrWhiteSpace(video.SubtitlePath)
                || !string.IsNullOrWhiteSpace(video.SubtitleSelectionPath)),
            VideoSmartRuleField.PlaybackState => MatchesText(rule, PlaybackTokens(video)),
            _ => false,
        };

    private static bool MatchesText(VideoSmartRule rule, params string?[] values)
    {
        var needle = rule.Value.Trim();
        if (needle.Length == 0)
            return false;

        return rule.Match switch
        {
            VideoSmartRuleMatch.Contains => values.Any(value => Contains(value, needle)),
            VideoSmartRuleMatch.Equals => values.Any(value =>
                string.Equals(value?.Trim(), needle, StringComparison.CurrentCultureIgnoreCase)),
            _ => false,
        };
    }

    private static bool MatchesBool(VideoSmartRule rule, bool value) =>
        rule.Match switch
        {
            VideoSmartRuleMatch.IsTrue => value,
            VideoSmartRuleMatch.Equals => value == IsTruthy(rule.Value),
            _ => false,
        };

    private static string[] PlaybackTokens(VideoItem video)
    {
        if (video.IsWatched)
            return ["finished", "watched", "played"];

        if (video.DurationSeconds <= 0 || video.LastPositionSeconds < VideoPlaybackState.MinimumPersistablePositionSeconds)
            return ["unwatched"];

        return ["inProgress", "in progress", "resumable", "started"];
    }

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(needle, StringComparison.CurrentCultureIgnoreCase);

    private static bool IsTruthy(string value) =>
        value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
        || value.Trim() == "1";

    private static IReadOnlyList<string> SplitTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
