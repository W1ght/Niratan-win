using System.Text.RegularExpressions;
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
        code.Should().Contain("await ViewModel.TickStatisticsAsync()");
        code.Should().Contain("if (!ViewModel.CanAcceptReaderPositionMutation)");
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
        readerCode.Should().Contain("SettleNavigationForLifecycleAsync");
        readerCode.Should().Contain("ApplyNavigationSettlement");
        readerCode.Should().Contain("WaitForTerminalRenderAsync");
        readerCode.Should().Contain("AcknowledgeNavigationRendered");
        readerCode.Should().Contain("CheckpointAppBackgroundingAsync");
        readerCode.Should().Contain("PrepareForReaderLifecycleCloseAsync");
        var lifecycleBody = Regex.Match(
            readerCode,
            @"(?s)private async Task<bool> HandleAppLifecycleCheckpointAsync\(.*?\n    \}").Value;
        lifecycleBody.Should().NotBeEmpty();
        lifecycleBody.Should().MatchRegex(new Regex(
            @"(?s)SettleNavigationForLifecycleAsync.*?ApplyNavigationSettlement.*?WaitForTerminalRenderAsync.*?(CheckpointAppBackgroundingAsync|PrepareForReaderLifecycleCloseAsync)"));
        lifecycleBody.Should().NotContain("ResetStatisticsBaselineAsync");
        readerCode.Should().MatchRegex(new Regex(
            @"(?s)case string id when id == ReaderShortcutActions\.Close\.Id:\s*await ViewModel\.BackToLibraryCommand\.ExecuteAsync\(null\);\s*return true;"));
        readerCode.Should().MatchRegex(new Regex(
            @"(?ms)^    protected override void OnNavigatedFrom\(NavigationEventArgs e\)\s*\{.*?_ = CompleteReaderLifecycleCloseAfterDetachAsync\(\);.*?^    \}"));
        var navigatedFromBody = Regex.Match(
            readerCode,
            @"(?ms)^    protected override void OnNavigatedFrom\(NavigationEventArgs e\)\s*\{.*?^    \}").Value;
        navigatedFromBody.IndexOf("WebMessageReceived -=", StringComparison.Ordinal)
            .Should().BeLessThan(navigatedFromBody.IndexOf(
                "CompleteReaderLifecycleCloseAfterDetachAsync",
                StringComparison.Ordinal));
        readerCode.Should().MatchRegex(new Regex(
            @"(?s)CompleteReaderLifecycleCloseAfterDetachAsync.*?SettleNavigationForLifecycleAsync.*?TryPrepareFailure.*?AcknowledgeNavigationRendered.*?CompleteFailure.*?PrepareForReaderLifecycleCloseAsync"));
    }
}
