using System.Text.Json;
using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoMiningHistoryStoreTests
{
    [Fact]
    public async Task RecordAsync_PersistsNewestItemsAndPrunesByLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"niratan-video-history-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VideoMiningHistoryStore(path, limit: 2);
            await store.RecordAsync(CreateCapture("first", 10), TestContext.Current.CancellationToken);
            await store.RecordAsync(CreateCapture("second", 20), TestContext.Current.CancellationToken);
            await store.RecordAsync(CreateCapture("third", 30), TestContext.Current.CancellationToken);

            store.Items.Select(item => item.SubtitleText).Should().Equal("second", "third");

            var reloaded = new VideoMiningHistoryStore(path, limit: 25);
            reloaded.Items.Select(item => item.SubtitleText).Should().Equal("second", "third");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdateLimitAsync_ZeroClearsAndDisablesHistory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"niratan-video-history-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VideoMiningHistoryStore(path, limit: 25);
            await store.RecordAsync(CreateCapture("first", 10), TestContext.Current.CancellationToken);

            await store.UpdateLimitAsync(0, TestContext.Current.CancellationToken);
            var id = await store.RecordAsync(CreateCapture("disabled", 20), TestContext.Current.CancellationToken);

            id.Should().BeNull();
            store.Items.Should().BeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RecordAsync_WritesNiratanCompatibleRemoteHistoryShape()
    {
        var path = Path.Combine(Path.GetTempPath(), $"niratan-video-history-{Guid.NewGuid():N}.json");
        try
        {
            var store = new VideoMiningHistoryStore(path);
            await store.RecordAsync(
                new VideoMiningHistoryCapture(
                    SubtitleText: "字幕",
                    VideoPath: "remote://youtube/abc123",
                    SubtitleSourceName: "Japanese",
                    SubtitleSourcePath: null,
                    SubtitleSelectionKind: VideoSubtitleSelectionKind.RemoteLanguage,
                    EmbeddedSubtitleTrackId: null,
                    CueStart: TimeSpan.FromSeconds(10),
                    CueEnd: TimeSpan.FromSeconds(12),
                    VideoTitle: "Remote title",
                    RemoteVideoIdentity: new RemoteVideoIdentity(
                        "youtube",
                        "abc123",
                        "https://youtu.be/abc123",
                        "https://www.youtube.com/watch?v=abc123",
                        "Remote title",
                        null,
                        TimeSpan.FromSeconds(120)),
                    SubtitleFormat: "webVTT"),
                TestContext.Current.CancellationToken);

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var item = document.RootElement[0];
            item.TryGetProperty("videoPath", out _).Should().BeFalse();
            item.GetProperty("videoTitle").GetString().Should().Be("Remote title");
            item.GetProperty("subtitleFormat").GetString().Should().Be("webVTT");
            item.GetProperty("cueStart").GetDouble().Should().Be(10);
            item.GetProperty("createdAt").ValueKind.Should().Be(JsonValueKind.Number);
            item.GetProperty("remoteVideoIdentity").GetProperty("providerID")
                .GetString().Should().Be("youtube");
            item.GetProperty("remoteVideoIdentity").GetProperty("duration")
                .GetDouble().Should().Be(120);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static VideoMiningHistoryCapture CreateCapture(string text, double startSeconds) =>
        new(
            SubtitleText: text,
            VideoPath: $@"D:\Anime\{text}.mkv",
            SubtitleSourceName: "Japanese",
            SubtitleSourcePath: null,
            SubtitleSelectionKind: VideoSubtitleSelectionKind.EmbeddedTrack,
            EmbeddedSubtitleTrackId: 7,
            CueStart: TimeSpan.FromSeconds(startSeconds),
            CueEnd: TimeSpan.FromSeconds(startSeconds + 2));
}
