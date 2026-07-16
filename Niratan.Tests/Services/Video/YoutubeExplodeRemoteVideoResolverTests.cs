using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class YoutubeExplodeRemoteVideoResolverTests
{
    [Fact]
    public async Task ResolveAsync_ReusesFreshCache_AndForceRefreshBypassesIt()
    {
        using var adapter = new FakeAdapter();
        using var resolver = new YoutubeExplodeRemoteVideoResolver(
            adapter,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000)));
        var ct = TestContext.Current.CancellationToken;

        var first = await resolver.ResolveAsync("https://youtu.be/yrL6Qny0E5M", ct: ct);
        var cached = await resolver.ResolveAsync("https://www.youtube.com/watch?v=yrL6Qny0E5M", ct: ct);
        var refreshed = await resolver.ResolveAsync(
            "https://youtu.be/yrL6Qny0E5M",
            forceRefresh: true,
            ct: ct);

        cached.Identity.RemoteId.Should().Be(first.Identity.RemoteId);
        refreshed.Should().NotBeSameAs(first);
        adapter.MetadataCalls.Should().Be(2);
        adapter.StreamCalls.Should().Be(2);
        adapter.SubtitleCalls.Should().Be(2);
    }

    [Fact]
    public async Task ResolveAsync_AppliesStartPositionToEachCachedRequest()
    {
        using var adapter = new FakeAdapter();
        using var resolver = new YoutubeExplodeRemoteVideoResolver(
            adapter,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_900_000_000)));
        var ct = TestContext.Current.CancellationToken;

        var first = await resolver.ResolveAsync(
            "https://www.youtube.com/watch?v=yrL6Qny0E5M&t=41s",
            ct: ct);
        var cached = await resolver.ResolveAsync(
            "https://youtu.be/yrL6Qny0E5M?t=82",
            ct: ct);

        first.RequestedStartPosition.Should().Be(TimeSpan.FromSeconds(41));
        cached.RequestedStartPosition.Should().Be(TimeSpan.FromSeconds(82));
        cached.Identity.OriginalUrl.Should().Be("https://youtu.be/yrL6Qny0E5M?t=82");
        adapter.MetadataCalls.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_PropagatesCancellation()
    {
        using var adapter = new FakeAdapter { WaitForCancellation = true };
        using var resolver = new YoutubeExplodeRemoteVideoResolver(adapter, TimeProvider.System);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var action = () => resolver.ResolveAsync("https://youtu.be/yrL6Qny0E5M", ct: cts.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeAdapter : IYoutubeExplodeClientAdapter
    {
        public int MetadataCalls { get; private set; }
        public int StreamCalls { get; private set; }
        public int SubtitleCalls { get; private set; }
        public bool WaitForCancellation { get; init; }

        public async Task<YouTubeRemoteMetadata> GetMetadataAsync(string videoId, CancellationToken ct)
        {
            MetadataCalls++;
            await WaitAsync(ct);
            return new YouTubeRemoteMetadata("Title", "https://example.test/thumb.jpg", TimeSpan.FromMinutes(3));
        }

        public async Task<IReadOnlyList<RemoteVideoStream>> GetStreamsAsync(string videoId, CancellationToken ct)
        {
            StreamCalls++;
            await WaitAsync(ct);
            return
            [
                Stream("video", 1080, true, false, "h264", null),
                Stream("audio", null, false, true, null, "aac"),
                Stream("muxed", 720, true, true, "h264", "aac"),
            ];
        }

        public async Task<IReadOnlyList<RemoteVideoSubtitleOption>> GetSubtitlesAsync(
            string videoId,
            CancellationToken ct)
        {
            SubtitleCalls++;
            await WaitAsync(ct);
            return [new RemoteVideoSubtitleOption("ja", "ja", "Japanese", "https://example.test/ja", false)];
        }

        public Task DownloadSubtitleAsync(
            RemoteVideoSubtitleOption option,
            string outputPath,
            CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }

        private Task WaitAsync(CancellationToken ct) =>
            WaitForCancellation ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : Task.CompletedTask;

        private static RemoteVideoStream Stream(
            string id,
            int? height,
            bool video,
            bool audio,
            string? videoCodec,
            string? audioCodec) =>
            new(
                $"https://example.test/{id}?expire=1900007200",
                id,
                height,
                video,
                audio,
                video ? "mp4" : "m4a",
                videoCodec,
                audioCodec,
                1000,
                new Dictionary<string, string>());
    }
}
