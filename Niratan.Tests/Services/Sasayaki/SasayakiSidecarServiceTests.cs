using System.Text.Json;
using FluentAssertions;
using Niratan.Models.Sasayaki;
using Niratan.Services.Sasayaki;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class SasayakiSidecarServiceTests
{
    [Fact]
    public async Task SaveMatchAsync_WritesPortableNiratanAndHoshiShape()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();

        await service.SaveMatchAsync(temp.Path, CreateMatchData(), ct);

        var matchPath = Path.Combine(temp.Path, ISasayakiSidecarService.MatchFileName);
        File.Exists(matchPath).Should().BeTrue();
        File.Exists(Path.Combine(temp.Path, ISasayakiSidecarService.LegacyMatchFileName))
            .Should().BeFalse();

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(matchPath, ct));
        var root = document.RootElement;
        root.EnumerateObject().Select(property => property.Name)
            .Should().BeEquivalentTo("matches", "unmatched");
        root.GetProperty("unmatched").GetInt32().Should().Be(1);

        var match = root.GetProperty("matches")[0];
        match.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(
            "id",
            "startTime",
            "endTime",
            "text",
            "chapterIndex",
            "start",
            "length");
        match.GetProperty("id").GetString().Should().Be("7");
        match.GetProperty("start").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task LoadMatchAsync_ReadsPortableNiratanAndHoshiShape()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, ISasayakiSidecarService.MatchFileName),
            """
            {
              "matches": [
                {
                  "id": "7",
                  "startTime": 1.2,
                  "endTime": 2.3,
                  "text": "星を読む",
                  "chapterIndex": 1,
                  "start": 42,
                  "length": 5
                }
              ],
              "unmatched": 1
            }
            """,
            ct);

        var loaded = await service.LoadMatchAsync(temp.Path, ct);

        loaded.Should().NotBeNull();
        loaded!.Unmatched.Should().Be(1);
        loaded.TotalCueCount.Should().Be(2);
        var match = loaded.Matches.Should().ContainSingle().Which;
        match.Id.Should().Be("7");
        match.StartTime.Should().Be(1.2);
        match.EndTime.Should().Be(2.3);
        match.Text.Should().Be("星を読む");
        match.ChapterIndex.Should().Be(1);
        match.Start.Should().Be(42);
        match.Length.Should().Be(5);
    }

    [Fact]
    public async Task LoadMatchAsync_FallsBackToPortableLegacyFileName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, ISasayakiSidecarService.LegacyMatchFileName),
            """{"matches":[],"unmatched":3}""",
            ct);

        var loaded = await service.LoadMatchAsync(temp.Path, ct);

        loaded.Should().NotBeNull();
        loaded!.Matches.Should().BeEmpty();
        loaded.Unmatched.Should().Be(3);
    }

    [Fact]
    public async Task LoadMatchAsync_MigratesLegacyWindowsV3AndPlaybackBookmark()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        var matchPath = Path.Combine(temp.Path, ISasayakiSidecarService.MatchFileName);
        await File.WriteAllTextAsync(
            matchPath,
            """
            {
              "schemaVersion": 3,
              "bookId": "book-1",
              "audiobookPath": "D:\\Books\\audio.m4b",
              "srtPath": "D:\\Books\\audio.srt",
              "cues": [
                {"id":1,"startTime":1.2,"endTime":2.3,"text":"星を読む"},
                {"id":2,"startTime":2.4,"endTime":2.9,"text":"未配准"},
                {"id":3,"startTime":3.0,"endTime":4.0,"text":"次の行"}
              ],
              "matches": [
                {"cueIndex":0,"chapterIndex":1,"startCodePoint":42,"length":5},
                {"cueIndex":2,"chapterIndex":1,"startCodePoint":47,"length":4}
              ],
              "totalChapters": 2,
              "unmatchedCount": 1
            }
            """,
            ct);
        await service.SavePlaybackAsync(
            temp.Path,
            new SasayakiPlaybackData { LastPosition = 3.2, AudioBookmark = 2 },
            ct);

        var loaded = await service.LoadMatchAsync(temp.Path, ct);

        loaded.Should().NotBeNull();
        loaded!.Matches.Select(match => match.Id).Should().Equal("0", "2");
        loaded.Matches.Select(match => match.Text).Should().Equal("星を読む", "次の行");
        loaded.Matches.Select(match => match.Start).Should().Equal(42, 47);
        loaded.Unmatched.Should().Be(1);

        var source = await service.LoadSourceAsync(temp.Path, ct);
        source.Should().NotBeNull();
        source!.AudiobookPath.Should().Be("D:\\Books\\audio.m4b");
        source.SrtPath.Should().Be("D:\\Books\\audio.srt");

        var playback = await service.LoadPlaybackAsync(temp.Path, ct);
        playback.LastPosition.Should().Be(3.2);
        playback.AudioBookmark.Should().Be(1);

        using var migrated = JsonDocument.Parse(await File.ReadAllTextAsync(matchPath, ct));
        migrated.RootElement.TryGetProperty("schemaVersion", out _).Should().BeFalse();
        migrated.RootElement.TryGetProperty("cues", out _).Should().BeFalse();
        migrated.RootElement.GetProperty("unmatched").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task LoadMatchAsync_IgnoresUnsupportedLegacyWindowsSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, ISasayakiSidecarService.MatchFileName),
            """{"schemaVersion":2,"cues":[],"matches":[],"unmatchedCount":0}""",
            ct);

        (await service.LoadMatchAsync(temp.Path, ct)).Should().BeNull();
    }

    [Fact]
    public async Task SourceSidecar_RoundTripsWindowsLocalPathsSeparately()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        var source = new SasayakiSourceData
        {
            AudiobookPath = "D:\\Books\\audio.m4b",
            SrtPath = "D:\\Books\\audio.srt",
        };

        await service.SaveSourceAsync(temp.Path, source, ct);

        var loaded = await service.LoadSourceAsync(temp.Path, ct);
        loaded.Should().BeEquivalentTo(source);
    }

    [Fact]
    public async Task SavePlaybackAsync_WritesWindowsPlaybackShape()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        var playback = new SasayakiPlaybackData
        {
            LastPosition = 12.5,
            Delay = -0.15,
            Rate = 1.25,
            AudioBookmark = 7,
        };

        await service.SavePlaybackAsync(temp.Path, playback, ct);

        var jsonPath = Path.Combine(temp.Path, ISasayakiSidecarService.PlaybackFileName);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, ct));
        var root = document.RootElement;
        root.GetProperty("lastPosition").GetDouble().Should().Be(12.5);
        root.GetProperty("delay").GetDouble().Should().Be(-0.15);
        root.GetProperty("rate").GetDouble().Should().Be(1.25);
        root.GetProperty("audioBookmark").GetInt32().Should().Be(7);
        root.TryGetProperty("positionSeconds", out _).Should().BeFalse();
        root.TryGetProperty("playbackRate", out _).Should().BeFalse();

        var loaded = await service.LoadPlaybackAsync(temp.Path, ct);
        loaded.Should().BeEquivalentTo(playback, options => options.Excluding(item => item.BookId));
    }

    [Fact]
    public async Task LoadPlaybackAsync_ReturnsDefaultWhenFileIsMissingOrMalformed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();

        var missing = await service.LoadPlaybackAsync(temp.Path, ct);
        missing.LastPosition.Should().Be(0);
        missing.Rate.Should().Be(1);
        missing.Delay.Should().Be(0);
        missing.AudioBookmark.Should().Be(-1);

        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, ISasayakiSidecarService.PlaybackFileName),
            "{ broken json",
            ct);

        var malformed = await service.LoadPlaybackAsync(temp.Path, ct);
        malformed.LastPosition.Should().Be(0);
        malformed.Rate.Should().Be(1);
        malformed.Delay.Should().Be(0);
        malformed.AudioBookmark.Should().Be(-1);
    }

    private static SasayakiMatchData CreateMatchData() => new()
    {
        Matches =
        [
            new SasayakiMatch
            {
                Id = "7",
                StartTime = 1.2,
                EndTime = 2.3,
                Text = "星を読む",
                ChapterIndex = 1,
                Start = 42,
                Length = 5,
            },
        ],
        Unmatched = 1,
    };

    private sealed class TempBookDirectory : IDisposable
    {
        public TempBookDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
