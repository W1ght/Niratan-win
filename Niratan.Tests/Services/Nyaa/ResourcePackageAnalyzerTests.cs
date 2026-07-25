using FluentAssertions;
using Niratan.Services.Nyaa;

namespace Niratan.Tests.Services.Nyaa;

public sealed class ResourcePackageAnalyzerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "niratan-nyaa-tests",
        Guid.NewGuid().ToString("N"));

    public ResourcePackageAnalyzerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Analyze_auto_matches_single_epub_audiobook_and_srt()
    {
        var epub = Touch("Example Novel.epub");
        var audio = Touch("Example Novel [Audiobook].m4b");
        var subtitle = Touch("Example Novel.srt");

        var result = new ResourcePackageAnalyzer().Analyze(_root);

        result.NovelMatch.Should().NotBeNull();
        result.NovelMatch!.EpubPath.Should().Be(epub);
        result.NovelMatch.AudiobookPath.Should().Be(audio);
        result.NovelMatch.SubtitlePath.Should().Be(subtitle);
        result.NovelMatch.Confidence.Should().Be(1);
    }

    [Fact]
    public void Analyze_selects_high_confidence_triple_from_multi_book_pack()
    {
        var targetEpub = Touch("吾輩は猫である.epub");
        Touch("別の本.epub");
        var targetAudio = Touch("吾輩は猫である audiobook.m4b");
        Touch("unrelated audio.flac");
        var targetSubtitle = Touch("吾輩は猫である.srt");
        Touch("different captions.srt");

        var result = new ResourcePackageAnalyzer().Analyze(_root);

        result.NovelMatch.Should().NotBeNull();
        result.NovelMatch!.EpubPath.Should().Be(targetEpub);
        result.NovelMatch.AudiobookPath.Should().Be(targetAudio);
        result.NovelMatch.SubtitlePath.Should().Be(targetSubtitle);
    }

    [Fact]
    public void Analyze_matches_video_language_suffix_subtitle()
    {
        var video = Touch(Path.Combine("Season 1", "Episode 01.mkv"));
        var subtitle = Touch(Path.Combine("Season 1", "Episode 01.ja.srt"));
        Touch(Path.Combine("Season 1", "Episode 02.srt"));

        var result = new ResourcePackageAnalyzer().Analyze(_root);

        result.VideoFiles.Should().ContainSingle().Which.Should().Be(video);
        result.VideoSubtitleMatches.Should().ContainKey(video)
            .WhoseValue.Should().Be(subtitle);
    }

    [Fact]
    public void Analyze_does_not_use_ass_for_sasayaki_matching()
    {
        Touch("Book.epub");
        Touch("Book.m4b");
        Touch("Book.ass");

        new ResourcePackageAnalyzer().Analyze(_root).NovelMatch.Should().BeNull();
    }

    private string Touch(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return Path.GetFullPath(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
