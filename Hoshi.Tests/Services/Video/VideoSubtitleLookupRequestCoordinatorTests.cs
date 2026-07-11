using FluentAssertions;
using Hoshi.Services.Video;

namespace Hoshi.Tests.Services.Video;

public class VideoSubtitleLookupRequestCoordinatorTests
{
    [Fact]
    public void BeginRequest_CancelsThePreviousRequestAndAdvancesVersion()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        var second = coordinator.BeginRequest();

        first.CancellationToken.IsCancellationRequested.Should().BeTrue();
        second.Version.Should().Be(first.Version + 1);
        coordinator.IsCurrent(first).Should().BeFalse();
        coordinator.IsCurrent(second).Should().BeTrue();
    }

    [Fact]
    public void CancelCurrent_InvalidatesTheCurrentRequest()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var request = coordinator.BeginRequest();

        coordinator.CancelCurrent();

        request.CancellationToken.IsCancellationRequested.Should().BeTrue();
        coordinator.IsCurrent(request).Should().BeFalse();
    }

    [Fact]
    public void PopupCommit_IsTakenOnlyForCurrentRequestAndTrace()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var request = coordinator.BeginRequest();
        coordinator.StagePopupCommit(request, "trace-1", 3, "師匠");

        coordinator.TryTakePopupCommit("other", out _).Should().BeFalse();
        coordinator.TryTakePopupCommit("trace-1", out var commit).Should().BeTrue();
        commit.Should().Be(new VideoSubtitlePopupCommit(3, "師匠"));
        coordinator.TryTakePopupCommit("trace-1", out _).Should().BeFalse();
    }

    [Fact]
    public void NewRequest_InvalidatesPreviousPendingPopupCommit()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "old", 0, "古い");

        coordinator.BeginRequest();

        coordinator.TryTakePopupCommit("old", out _).Should().BeFalse();
    }

    [Fact]
    public void CancelCurrent_InvalidatesPendingPopupCommit()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var request = coordinator.BeginRequest();
        coordinator.StagePopupCommit(request, "late", 2, "遅い");

        coordinator.CancelCurrent();

        coordinator.TryTakePopupCommit("late", out _).Should().BeFalse();
    }

    [Fact]
    public void RapidClickThenShiftHover_OnlyTakesTheLatestPopupCommit()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var click = coordinator.BeginRequest();
        coordinator.StagePopupCommit(click, "click", 0, "最初");

        var hover = coordinator.BeginRequest();
        coordinator.StagePopupCommit(hover, "hover", 4, "最新");

        coordinator.TryTakePopupCommit("click", out _).Should().BeFalse();
        coordinator.TryTakePopupCommit("hover", out var commit).Should().BeTrue();
        commit.Should().Be(new VideoSubtitlePopupCommit(4, "最新"));
    }

    [Fact]
    public void StaleRequest_CannotStageAfterAReplacementBegins()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var stale = coordinator.BeginRequest();
        var current = coordinator.BeginRequest();

        coordinator.StagePopupCommit(stale, "stale", 0, "古い");
        coordinator.StagePopupCommit(current, "current", 2, "新しい");

        coordinator.TryTakePopupCommit("stale", out _).Should().BeFalse();
        coordinator.TryTakePopupCommit("current", out _).Should().BeTrue();
    }

    [Fact]
    public void LatestRequestWithNoResult_DoesNotReplaceTheLastTakenCommit()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "表示中");
        coordinator.TryTakePopupCommit("first", out var committed).Should().BeTrue();

        var noResult = coordinator.BeginRequest();

        coordinator.IsCurrent(noResult).Should().BeTrue();
        coordinator.TryTakePopupCommit("first", out _).Should().BeFalse();
        committed.Should().Be(new VideoSubtitlePopupCommit(1, "表示中"));
    }

    [Fact]
    public void LatestRequestError_InvalidatesItsCommitWithoutChangingTheLastTakenCommit()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "表示中");
        coordinator.TryTakePopupCommit("first", out var committed).Should().BeTrue();
        var failing = coordinator.BeginRequest();
        coordinator.StagePopupCommit(failing, "failing", 5, "失敗");

        coordinator.CancelCurrent();

        coordinator.TryTakePopupCommit("failing", out _).Should().BeFalse();
        committed.Should().Be(new VideoSubtitlePopupCommit(1, "表示中"));
    }
}
