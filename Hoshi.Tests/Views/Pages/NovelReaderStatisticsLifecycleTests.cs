using FluentAssertions;

namespace Hoshi.Tests.Views.Pages;

public sealed class NovelReaderStatisticsLifecycleTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Hoshi"));

    [Fact]
    public void ReaderPage_ProjectsStatisticsEverySecondOnlyWhileActivelyTracking()
    {
        var code = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));

        code.Should().Contain("CreateTimer()");
        code.Should().Contain("Interval = TimeSpan.FromSeconds(1)");
        code.Should().Contain("ViewModel.IsStatisticsTracking && !ViewModel.IsStatisticsPaused");
        code.Should().Contain("ViewModel.TickStatistics()");
        code.Should().Contain("if (_programmaticNavigation.HasPending)");
        code.Should().Contain("StopStatisticsProjectionTimer");
    }

    [Fact]
    public void MainWindowAndReaderPage_AwaitSharedLifecycleCheckpointBoundary()
    {
        var windowCode = File.ReadAllText(Path.Combine(ProjectRoot, "MainWindow.xaml.cs"));
        var readerCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Pages", "NovelReaderPage.xaml.cs"));
        var messageCode = File.ReadAllText(
            Path.Combine(ProjectRoot, "Messages", "AppBackgroundingMessage.cs"));

        messageCode.Should().Contain("AsyncRequestMessage<bool>");
        messageCode.Should().Contain("AppLifecycleCheckpointReason");
        windowCode.Should().Contain("AppWindow.Changed += MainWindow_AppWindowChanged");
        windowCode.Should().Contain("OverlappedPresenterState.Minimized");
        windowCode.Should().Contain("await SendAppLifecycleCheckpointAsync(");
        windowCode.Should().Contain("AppLifecycleCheckpointReason.Closing");
        readerCode.Should().Contain("Register<AppBackgroundingMessage>");
        readerCode.Should().Contain("CheckpointAppBackgroundingAsync");
        readerCode.Should().Contain("PrepareForReaderLifecycleCloseAsync");
    }
}
