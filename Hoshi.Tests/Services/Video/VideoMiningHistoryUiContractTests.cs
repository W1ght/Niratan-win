using FluentAssertions;

namespace Hoshi.Tests.Services.Video;

public class VideoMiningHistoryUiContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Hoshi"));

    [Fact]
    public void VideoPlayerWindow_MiningHistoryTabHasStoreBackedActions()
    {
        var xaml = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(ProjectRoot, "Views", "Video", "VideoPlayerWindow.xaml.cs"));

        xaml.Should().Contain("x:Name=\"MiningHistoryListView\"");
        xaml.Should().Contain("ItemsSource=\"{x:Bind ViewModel.MiningHistoryRows, Mode=OneWay}\"");
        xaml.Should().Contain("RecordMiningHistoryButton_Click");
        xaml.Should().Contain("ClearMiningHistoryButton_Click");
        xaml.Should().Contain("CopyMiningHistoryButton_Click");
        xaml.Should().Contain("DeleteMiningHistoryButton_Click");
        xaml.Should().Contain("暂无挖卡历史。保存当前字幕后会显示在这里。");
        xaml.Should().NotContain("挖卡历史记录源接入后会显示在这里。");

        code.Should().Contain("IVideoMiningHistoryStore");
        code.Should().Contain("MiningHistoryListView_ItemClick");
        code.Should().Contain("RecordCurrentMiningHistoryAsync");
    }
}
