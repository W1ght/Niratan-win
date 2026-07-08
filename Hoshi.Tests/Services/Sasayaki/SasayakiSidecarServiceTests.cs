using System.Text.Json;
using FluentAssertions;
using Hoshi.Models.Sasayaki;
using Hoshi.Services.Sasayaki;

namespace Hoshi.Tests.Services.Sasayaki;

public sealed class SasayakiSidecarServiceTests
{
    [Fact]
    public async Task SaveMatchAsync_WritesMacCompatibleMatchSidecarName()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        var data = CreateMatchData();

        await service.SaveMatchAsync(temp.Path, data, ct);

        File.Exists(System.IO.Path.Combine(temp.Path, "sasayaki_match.json")).Should().BeTrue();
        File.Exists(System.IO.Path.Combine(temp.Path, "sasayaki.json")).Should().BeFalse();

        var loaded = await service.LoadMatchAsync(temp.Path, ct);
        loaded.Should().NotBeNull();
        loaded!.BookId.Should().Be("book-1");
        loaded.Matches.Should().ContainSingle().Which.StartCodePoint.Should().Be(42);
    }

    [Fact]
    public async Task LoadMatchAsync_FallsBackToLegacySasayakiJson()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        var data = CreateMatchData();
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "sasayaki.json"), json, ct);

        var loaded = await service.LoadMatchAsync(temp.Path, ct);

        loaded.Should().NotBeNull();
        loaded!.BookId.Should().Be("book-1");
        loaded.Matches.Should().ContainSingle().Which.Length.Should().Be(5);
    }

    [Fact]
    public async Task LoadMatchAsync_IgnoresStaleMatcherSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        var data = CreateMatchData();
        data.SchemaVersion = SasayakiMatchData.CurrentSchemaVersion - 1;
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(System.IO.Path.Combine(temp.Path, "sasayaki_match.json"), json, ct);

        var loaded = await service.LoadMatchAsync(temp.Path, ct);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SavePlaybackAsync_WritesMacCompatiblePlaybackShape()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempBookDirectory();
        var service = new SasayakiSidecarService();
        var playback = new SasayakiPlaybackData
        {
            LastPosition = 12.5,
            Delay = -0.15,
            Rate = 1.25,
        };

        await service.SavePlaybackAsync(temp.Path, playback, ct);

        var jsonPath = System.IO.Path.Combine(temp.Path, "sasayaki_playback.json");
        File.Exists(jsonPath).Should().BeTrue();
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(jsonPath, ct));
        var root = document.RootElement;
        root.GetProperty("lastPosition").GetDouble().Should().Be(12.5);
        root.GetProperty("delay").GetDouble().Should().Be(-0.15);
        root.GetProperty("rate").GetDouble().Should().Be(1.25);
        root.TryGetProperty("positionSeconds", out _).Should().BeFalse();
        root.TryGetProperty("playbackRate", out _).Should().BeFalse();

        var loaded = await service.LoadPlaybackAsync(temp.Path, ct);
        loaded.LastPosition.Should().Be(12.5);
        loaded.Delay.Should().Be(-0.15);
        loaded.Rate.Should().Be(1.25);
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

        await File.WriteAllTextAsync(
            System.IO.Path.Combine(temp.Path, "sasayaki_playback.json"),
            "{ broken json",
            ct);

        var malformed = await service.LoadPlaybackAsync(temp.Path, ct);
        malformed.LastPosition.Should().Be(0);
        malformed.Rate.Should().Be(1);
        malformed.Delay.Should().Be(0);
    }

    private static SasayakiMatchData CreateMatchData() => new()
    {
        BookId = "book-1",
        AudiobookPath = "D:\\Books\\audio.m4b",
        SrtPath = "D:\\Books\\audio.srt",
        TotalChapters = 2,
        Cues =
        [
            new SasayakiCue
            {
                Id = 1,
                StartTime = 1.2,
                EndTime = 2.3,
                Text = "星を読む",
            },
        ],
        Matches =
        [
            new SasayakiMatch
            {
                CueIndex = 0,
                ChapterIndex = 1,
                StartCodePoint = 42,
                Length = 5,
            },
        ],
    };

    private sealed class TempBookDirectory : IDisposable
    {
        public TempBookDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
