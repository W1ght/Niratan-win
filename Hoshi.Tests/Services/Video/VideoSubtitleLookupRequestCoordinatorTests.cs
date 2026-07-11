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
    public void NormalPendingCancellation_RemovesPreviousPopupCommit()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "old", 0, "古い");

        coordinator.BeginRequest();
        coordinator.CancelPopupCommit("old");

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
        coordinator.CancelPopupCommit("click");

        coordinator.TryTakePopupCommit("click", out _).Should().BeFalse();
        coordinator.TryTakePopupCommit("hover", out var commit).Should().BeTrue();
        commit.Should().Be(new VideoSubtitlePopupCommit(4, "最新"));
    }

    [Fact]
    public void SameSourceTrace_ProducesRequestUniquePopupCommitIdentities()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        var firstIdentity = coordinator.CreatePopupCommitIdentity(first, "same-trace");
        var second = coordinator.BeginRequest();
        var secondIdentity = coordinator.CreatePopupCommitIdentity(second, "same-trace");

        firstIdentity.Should().NotBe(secondIdentity);
        firstIdentity.Should().Contain(first.Version.ToString());
        secondIdentity.Should().Contain(second.Version.ToString());
    }

    [Fact]
    public void SameSourceTrace_LateFirstCommitCannotConsumeLatestCandidate()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        var firstIdentity = coordinator.CreatePopupCommitIdentity(first, "same-trace");
        coordinator.StagePopupCommit(first, firstIdentity, 1, "最初");
        var latest = coordinator.BeginRequest();
        var latestIdentity = coordinator.CreatePopupCommitIdentity(latest, "same-trace");
        coordinator.StagePopupCommit(latest, latestIdentity, 4, "最新");
        coordinator.MarkPopupCommitAccepted(firstIdentity);

        coordinator.TryTakePopupCommit(firstIdentity, out var firstCommit).Should().BeTrue();
        coordinator.TryTakePopupCommit(latestIdentity, out var latestCommit).Should().BeTrue();

        firstCommit.Should().Be(new VideoSubtitlePopupCommit(1, "最初"));
        latestCommit.Should().Be(new VideoSubtitlePopupCommit(4, "最新"));
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
    public void AcceptedFirstRequest_ThenLatestNoResult_CommitsTheFirstCandidate()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "表示中");

        var noResult = coordinator.BeginRequest();
        coordinator.MarkPopupCommitAccepted("first");

        coordinator.IsCurrent(noResult).Should().BeTrue();
        coordinator.TryTakePopupCommit("first", out var committed).Should().BeTrue();
        committed.Should().Be(new VideoSubtitlePopupCommit(1, "表示中"));
    }

    [Fact]
    public void RootCommitBeforeCancellationResult_CompletesTheExactSupersededCandidate()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "表示中");

        coordinator.BeginRequest();

        coordinator.TryTakePopupCommit("first", out var committed).Should().BeTrue();
        committed.Should().Be(new VideoSubtitlePopupCommit(1, "表示中"));
    }

    [Fact]
    public void AcceptedFirstRequest_ThenLatestError_CommitsFirstAndRejectsLatest()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "表示中");
        var failing = coordinator.BeginRequest();
        coordinator.StagePopupCommit(failing, "failing", 5, "失敗");
        coordinator.MarkPopupCommitAccepted("first");

        coordinator.CancelCurrentRequest();
        coordinator.CancelPopupCommit("failing");

        coordinator.TryTakePopupCommit("first", out var committed).Should().BeTrue();
        coordinator.TryTakePopupCommit("failing", out _).Should().BeFalse();
        committed.Should().Be(new VideoSubtitlePopupCommit(1, "表示中"));
    }

    [Fact]
    public void AcceptedFirstRequest_ThenLatestBecomesStale_CommitsFirstOnly()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "表示中");
        var stale = coordinator.BeginRequest();
        coordinator.StagePopupCommit(stale, "stale", 5, "古い");
        coordinator.MarkPopupCommitAccepted("first");

        coordinator.BeginRequest();
        coordinator.CancelPopupCommit("stale");

        coordinator.TryTakePopupCommit("first", out var committed).Should().BeTrue();
        coordinator.TryTakePopupCommit("stale", out _).Should().BeFalse();
        committed.Should().Be(new VideoSubtitlePopupCommit(1, "表示中"));
    }

    [Fact]
    public void AcceptedFirstRequest_ThenLatestSuccess_CommitsBothInRendererOrder()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "最初");
        var latest = coordinator.BeginRequest();
        coordinator.StagePopupCommit(latest, "latest", 4, "最新");
        coordinator.MarkPopupCommitAccepted("first");

        coordinator.TryTakePopupCommit("first", out var firstCommit).Should().BeTrue();
        coordinator.TryTakePopupCommit("latest", out var latestCommit).Should().BeTrue();

        firstCommit.Should().Be(new VideoSubtitlePopupCommit(1, "最初"));
        latestCommit.Should().Be(new VideoSubtitlePopupCommit(4, "最新"));
    }

    [Fact]
    public void AcceptedCommitAbort_RemovesOnlyTheMatchingCandidate()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "最初");
        var latest = coordinator.BeginRequest();
        coordinator.StagePopupCommit(latest, "latest", 4, "最新");
        coordinator.MarkPopupCommitAccepted("first");

        coordinator.CancelPopupCommit("first");

        coordinator.TryTakePopupCommit("first", out _).Should().BeFalse();
        coordinator.TryTakePopupCommit("latest", out _).Should().BeTrue();
    }

    [Fact]
    public void ExplicitDismiss_ClearsAcceptedAndLatestCandidates()
    {
        using var coordinator = new VideoSubtitleLookupRequestCoordinator();
        var first = coordinator.BeginRequest();
        coordinator.StagePopupCommit(first, "first", 1, "最初");
        var latest = coordinator.BeginRequest();
        coordinator.StagePopupCommit(latest, "latest", 4, "最新");
        coordinator.MarkPopupCommitAccepted("first");

        coordinator.CancelCurrent();

        coordinator.TryTakePopupCommit("first", out _).Should().BeFalse();
        coordinator.TryTakePopupCommit("latest", out _).Should().BeFalse();
    }
}
