using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Services.Dictionary;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class VideoPlayerViewModelRemoteSubtitleTests
{
    [Fact]
    public void ConfigureRemoteSource_DoesNotMarkPreferredTrackActiveBeforeItLoads()
    {
        var sut = CreateSut();

        sut.ConfigureRemoteSource(Source("ja:0", "en:1", selectedLanguage: "ja"));

        sut.RemoteSubtitleOptions.Should().HaveCount(2);
        sut.SelectedRemoteSubtitleId.Should().BeNull();
    }

    [Fact]
    public async Task LoadAndReconfigureRemoteSubtitle_TracksTheActuallyActiveLanguage()
    {
        var sut = CreateSut();
        var source = Source("ja:0", "en:1", selectedLanguage: "ja");
        sut.ConfigureRemoteSource(source);
        var japanesePath = Path.Combine(Path.GetTempPath(), $"niratan-remote-subtitle-ja-{Guid.NewGuid():N}.srt");
        var englishPath = Path.Combine(Path.GetTempPath(), $"niratan-remote-subtitle-en-{Guid.NewGuid():N}.srt");
        try
        {
            await File.WriteAllTextAsync(
                japanesePath,
                "1\n00:00:00,000 --> 00:00:10,000\n日本語字幕\n",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                englishPath,
                "1\n00:00:00,000 --> 00:00:10,000\nEnglish subtitle\n",
                TestContext.Current.CancellationToken);

            await sut.LoadRemoteSubtitleAsync(
                japanesePath,
                source.SubtitleOptions[0],
                TestContext.Current.CancellationToken);

            sut.SelectedRemoteSubtitleId.Should().Be("ja:0");
            sut.GetCurrentSubtitleSelection().Should().Be(VideoSubtitleSelection.RemoteLanguage("ja"));
            sut.CurrentSubtitleText.Should().Be("日本語字幕");

            sut.ConfigureRemoteSource(Source("ja:8", "en:4", selectedLanguage: "en"));
            sut.SelectedRemoteSubtitleId.Should().Be("ja:8");

            var refreshedEnglish = sut.RemoteSubtitleOptions.Single(option => option.Language == "en");
            await sut.LoadRemoteSubtitleAsync(
                englishPath,
                refreshedEnglish,
                TestContext.Current.CancellationToken);
            sut.SelectedRemoteSubtitleId.Should().Be("en:4");
            sut.GetCurrentSubtitleSelection().Should().Be(VideoSubtitleSelection.RemoteLanguage("en"));
            sut.CurrentSubtitleText.Should().Be("English subtitle");
        }
        finally
        {
            File.Delete(japanesePath);
            File.Delete(englishPath);
        }
    }

    private static VideoPlayerViewModel CreateSut() =>
        new(new SubtitleParserService(), Mock.Of<IDictionaryPopupRequestService>());

    private static ResolvedRemoteVideoSource Source(
        string japaneseId,
        string englishId,
        string selectedLanguage)
    {
        var stream = new RemoteVideoStream(
            "https://example.test/video",
            "137",
            1080,
            true,
            false,
            "mp4",
            "h264",
            null,
            1_000_000,
            new Dictionary<string, string>());
        var audio = new RemoteVideoStream(
            "https://example.test/audio",
            "140",
            null,
            false,
            true,
            "m4a",
            null,
            "aac",
            128_000,
            new Dictionary<string, string>());
        var quality = new RemoteVideoQualityOption("1080", 1080, stream, audio);
        return new ResolvedRemoteVideoSource(
            new RemoteVideoIdentity(
                "youtube",
                "yrL6Qny0E5M",
                "https://youtu.be/yrL6Qny0E5M",
                "https://www.youtube.com/watch?v=yrL6Qny0E5M",
                "Title",
                null,
                TimeSpan.FromMinutes(3)),
            stream,
            audio,
            null,
            audio,
            [
                new RemoteVideoSubtitleOption(japaneseId, "ja", "Japanese", "https://example.test/ja", false),
                new RemoteVideoSubtitleOption(englishId, "en", "English", "https://example.test/en", false),
            ],
            selectedLanguage,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddHours(1),
            [quality]);
    }
}
