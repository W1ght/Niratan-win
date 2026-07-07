using FluentAssertions;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class VideoLibrarySubtitleAutoloadTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"hoshi-subtitle-autoload-{Guid.NewGuid():N}");

    public VideoLibrarySubtitleAutoloadTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void FindSidecarSubtitle_FollowsNiratanExactSubtitlePriority()
    {
        var mediaPath = Touch("Episode 01.mkv");
        var exactSrt = Touch("Episode 01.srt");
        var exactVtt = Touch("Episode 01.vtt");
        var exactAss = Touch("Episode 01.ass");
        var exactSsa = Touch("Episode 01.ssa");

        VideoLibraryService.FindSidecarSubtitle(mediaPath).Should().Be(exactSrt);

        File.Delete(exactSrt);
        VideoLibraryService.FindSidecarSubtitle(mediaPath).Should().Be(exactVtt);

        File.Delete(exactVtt);
        VideoLibraryService.FindSidecarSubtitle(mediaPath).Should().Be(exactAss);

        File.Delete(exactAss);
        VideoLibraryService.FindSidecarSubtitle(mediaPath).Should().Be(exactSsa);
    }

    [Fact]
    public void FindSidecarSubtitle_AcceptsLanguageSuffixedCandidates()
    {
        var mediaPath = Touch("Episode 02.mp4");
        var languageSrt = Touch("Episode 02.ja.srt");
        Touch("Episode 03.srt");

        VideoLibraryService.FindSidecarSubtitle(mediaPath).Should().Be(languageSrt);
    }

    [Fact]
    public void FindSidecarSubtitle_AcceptsLanguageSuffixedAssCandidates()
    {
        var mediaPath = Touch("Episode 04.mp4");
        var languageAss = Touch("Episode 04.ja.ass");
        Touch("Episode 03.srt");

        VideoLibraryService.FindSidecarSubtitle(mediaPath).Should().Be(languageAss);
    }

    [Fact]
    public void FindSidecarSubtitle_DoesNotPickUnrelatedSidecars()
    {
        var mediaPath = Touch("No Match.mp4");
        Touch("Episode 03.srt");

        VideoLibraryService.FindSidecarSubtitle(mediaPath).Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string Touch(string fileName)
    {
        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, "");
        return path;
    }
}
