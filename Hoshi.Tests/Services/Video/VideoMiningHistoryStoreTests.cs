using FluentAssertions;
using Hoshi.Models;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class VideoMiningHistoryStoreTests
{
    [Fact]
    public async Task RecordAsync_PersistsNewestItemsAndPrunesByLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hoshi-video-history-{Guid.NewGuid():N}.json");
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
        var path = Path.Combine(Path.GetTempPath(), $"hoshi-video-history-{Guid.NewGuid():N}.json");
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
