using FluentAssertions;
using Niratan.Services.Anki;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoMiningContextFactoryTests
{
    [Fact]
    public void Create_BuildsVideoAnkiContextFromCue()
    {
        var cue = new VideoSubtitleCue(
            1,
            TimeSpan.FromSeconds(12),
            TimeSpan.FromMilliseconds(14500),
            "星がきれいですね。");
        var context = new VideoSubtitleCueContext(
            cue,
            PreviousText: "夜空を見て。",
            NextText: "本当に。");

        var result = VideoMiningContextFactory.Create(
            "D:\\Anime\\episode 01.mkv",
            TimeSpan.FromMilliseconds(12345),
            context,
            "D:\\Media\\shot.webp",
            "D:\\Media\\clip.mp3",
            sentenceOffset: 0);

        result.Sentence.Should().Be("星がきれいですね。");
        result.DocumentTitle.Should().Be("episode 01");
        result.VideoFileName.Should().Be("episode 01.mkv");
        result.VideoTimestamp.Should().Be("00:00:12.345");
        result.VideoCueStart.Should().Be("00:00:12.000");
        result.VideoCueEnd.Should().Be("00:00:14.500");
        result.VideoPreviousSubtitle.Should().Be("夜空を見て。");
        result.VideoNextSubtitle.Should().Be("本当に。");
        result.VideoScreenshotPath.Should().Be("D:\\Media\\shot.webp");
        result.VideoAudioClipPath.Should().Be("D:\\Media\\clip.mp3");
    }

    [Fact]
    public void AnkiRenderer_RendersVideoPlaceholders()
    {
        var context = new Niratan.Models.Anki.AnkiMiningContext
        {
            VideoFileName = "episode 01.mkv",
            VideoTimestamp = "00:00:12.345",
            VideoCueStart = "00:00:12.000",
            VideoCueEnd = "00:00:14.500",
            VideoSubtitle = "星がきれいですね。",
            VideoPreviousSubtitle = "夜空を見て。",
            VideoNextSubtitle = "本当に。",
            VideoScreenshotTag = "<img src=\"shot.webp\">",
            VideoAudioClipTag = "[sound:clip.mp3]",
        };

        var rendered = AnkiHandlebarRenderer.Render(
            "{video-file-name}|{video-timestamp}|{video-cue-start}|{video-cue-end}|{video-subtitle}|{video-previous-subtitle}|{video-next-subtitle}|{video-screenshot}|{video-audio-clip}",
            new Niratan.Models.Anki.AnkiMiningPayload(),
            context);

        rendered.Should().Be("episode 01.mkv|00:00:12.345|00:00:12.000|00:00:14.500|星がきれいですね。|夜空を見て。|本当に。|<img src=\"shot.webp\">|[sound:clip.mp3]");
        AnkiHandlebarRenderer.GetHandlebarOptions().Should().Contain("{video-audio-clip}");
    }

    [Fact]
    public void MediaNaming_UsesStableSafeNames()
    {
        var screenshot = VideoMiningMediaNaming.CreateScreenshotFilename(
            "D:\\Anime\\episode 01.mkv",
            TimeSpan.FromSeconds(12));
        var audio = VideoMiningMediaNaming.CreateAudioClipFilename(
            "D:\\Anime\\episode 01.mkv",
            TimeSpan.FromSeconds(12),
            TimeSpan.FromSeconds(14.5));

        screenshot.Should().StartWith("niratan_video_");
        screenshot.Should().EndWith("_000012000.webp");
        audio.Should().EndWith("_000012000_000014500.m4a");
        screenshot.Should().NotBe(audio);
    }
}
