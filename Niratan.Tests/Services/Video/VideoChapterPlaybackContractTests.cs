using FluentAssertions;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public class VideoChapterPlaybackContractTests
{
    [Fact]
    public void PlaybackEngine_ExposesChapterListAndChapterSeek()
    {
        typeof(IVideoPlaybackEngine).GetMethod("GetChaptersAsync").Should().NotBeNull();
        typeof(IVideoPlaybackEngine).GetMethod("SeekChapterAsync").Should().NotBeNull();
    }

    [Fact]
    public void VideoPlayerWindow_ChaptersTabUsesChapterRowsInsteadOfEpisodeRows()
    {
        var projectRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));
        var xaml = File.ReadAllText(Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"))
                   + File.ReadAllText(Path.Combine(projectRoot, "Views", "Video", "VideoPlayerWindow.Inspector.cs"));

        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.ChapterRows, Mode=OneWay}\"");
        xaml.Should().Contain("x:Uid=\"VideoInspectorChaptersHeaderText\"");
        xaml.Should().Contain("x:Uid=\"VideoInspectorNoChaptersText\"");
        xaml.Should().Contain("Text=\"暂无章节\"");
        xaml.Should().NotContain("ItemsSource=\"{x:Bind ViewModel.EpisodeRows, Mode=OneWay}\"");
        xaml.Should().NotContain("x:Uid=\"VideoInspectorEpisodesHeaderText\"\r\n                                                           Grid.Column=\"1\"\r\n                                                           Text=\"章节\"");
        xaml.Should().NotContain("x:Uid=\"VideoInspectorNoEpisodesText\"\r\n                                                       Text=\"暂无章节\"");
        code.Should().Contain("ChapterListView_ItemClick");
        code.Should().Contain("SeekChapterAsync");
    }
}
