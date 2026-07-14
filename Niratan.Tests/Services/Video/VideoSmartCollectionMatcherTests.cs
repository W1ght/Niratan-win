using FluentAssertions;
using Niratan.Models;
using Niratan.Models.Video;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoSmartCollectionMatcherTests
{
    [Fact]
    public void Matches_AllRulesAgainstVideoMetadata()
    {
        var video = new VideoItem
        {
            Title = "Episode 12",
            FilePath = @"D:\Anime\Umaru\Season 1\Episode 12.mkv",
            SourceFolderPath = @"D:\Anime\Umaru\Season 1",
            Tags = "anime, comedy",
            SubtitlePath = @"D:\Anime\Umaru\Season 1\Episode 12.ass",
            LastPositionSeconds = 40,
            DurationSeconds = 100,
        };
        var rules = new[]
        {
            new VideoSmartRule { Field = VideoSmartRuleField.FileName, Match = VideoSmartRuleMatch.Contains, Value = "episode" },
            new VideoSmartRule { Field = VideoSmartRuleField.Tag, Match = VideoSmartRuleMatch.Contains, Value = "anime" },
            new VideoSmartRule { Field = VideoSmartRuleField.HasBoundSubtitle, Match = VideoSmartRuleMatch.IsTrue },
            new VideoSmartRule { Field = VideoSmartRuleField.PlaybackState, Match = VideoSmartRuleMatch.Equals, Value = "inProgress" },
        };

        Niratan.Services.Video.VideoSmartCollectionMatcher.Matches(video, rules).Should().BeTrue();
    }

    [Fact]
    public void Matches_ReturnsFalseWhenAnyRuleDoesNotMatch()
    {
        var video = new VideoItem
        {
            Title = "Episode 12",
            FilePath = @"D:\Anime\Umaru\Season 1\Episode 12.mkv",
            SourceFolderPath = @"D:\Anime\Umaru\Season 1",
            Tags = "anime, comedy",
            LastPositionSeconds = 1,
            DurationSeconds = 100,
        };
        var rules = new[]
        {
            new VideoSmartRule { Field = VideoSmartRuleField.FileName, Match = VideoSmartRuleMatch.Contains, Value = "episode" },
            new VideoSmartRule { Field = VideoSmartRuleField.PlaybackState, Match = VideoSmartRuleMatch.Equals, Value = "inProgress" },
        };

        Niratan.Services.Video.VideoSmartCollectionMatcher.Matches(video, rules).Should().BeFalse();
    }

    [Fact]
    public void Matches_ReturnsFalseForEmptyRules()
    {
        Niratan.Services.Video.VideoSmartCollectionMatcher.Matches(new VideoItem(), []).Should().BeFalse();
    }
}
