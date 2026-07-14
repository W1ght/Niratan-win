using FluentAssertions;
using Niratan.Services.Dictionary;

namespace Niratan.Tests.Services.Dictionary;

public class DictionaryPopupNavigationStateCoordinatorTests
{
    [Fact]
    public void NavigationStateCoordinator_IsFrameworkIndependentService()
    {
        var type = typeof(DictionaryPopupDisplayTransaction).Assembly.GetType(
            "Niratan.Services.Dictionary.DictionaryPopupNavigationStateCoordinator");

        type.Should().NotBeNull();
        type!.GetMethod("CommitRoot").Should().NotBeNull();
        type.GetMethod("TryUpdate").Should().NotBeNull();
        type.GetMethod("IsCurrent").Should().NotBeNull();
        type.GetMethod("SetVisibility").Should().NotBeNull();
        type.GetMethod("Reset").Should().NotBeNull();
        type!.GetProperties()
            .Select(property => property.PropertyType.Namespace)
            .Should()
            .NotContain(@namespace => @namespace != null
                && @namespace.StartsWith("Microsoft.UI", StringComparison.Ordinal));
    }

    [Fact]
    public void CommittedA_AcceptsNavigationStateAfterPendingBAborts()
    {
        var display = new DictionaryPopupDisplayTransaction();
        var navigation = new DictionaryPopupNavigationStateCoordinator();
        display.TryBeginPending(10, "A", out _).Should().BeTrue();
        display.TryAcceptCommit(10).Should().BeTrue();
        display.TryCompleteCommit(10, out _).Should().BeTrue();
        navigation.CommitRoot(documentEpoch: 3, generation: 10);

        display.TryBeginPending(11, "B", out _).Should().BeTrue();
        display.TryAcceptCommit(11).Should().BeTrue();
        display.TryAbortCommit(11).Should().BeTrue();

        display.CommittedGeneration.Should().Be(10);
        navigation.TryUpdate(3, 10, canGoBack: true, canGoForward: false)
            .Should().BeTrue();
        navigation.CanGoBack.Should().BeTrue();
        navigation.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void NavigationState_RejectsMismatchedEpochOrGeneration()
    {
        var navigation = new DictionaryPopupNavigationStateCoordinator();
        navigation.CommitRoot(documentEpoch: 5, generation: 20);

        navigation.TryUpdate(4, 20, true, true).Should().BeFalse();
        navigation.TryUpdate(5, 19, true, true).Should().BeFalse();

        navigation.CanGoBack.Should().BeFalse();
        navigation.CanGoForward.Should().BeFalse();
        navigation.IsCurrent(5, 20).Should().BeTrue();
        navigation.IsCurrent(4, 20).Should().BeFalse();
        navigation.IsCurrent(5, 19).Should().BeFalse();
    }

    [Fact]
    public void RedirectStateAndVisibilityContinuation_PreserveButtonsInEitherOrder()
    {
        var messageFirst = new DictionaryPopupNavigationStateCoordinator();
        messageFirst.CommitRoot(7, 30);
        messageFirst.TryUpdate(7, 30, canGoBack: true, canGoForward: false)
            .Should().BeTrue();
        messageFirst.SetVisibility(true);

        messageFirst.IsVisible.Should().BeTrue();
        messageFirst.CanGoBack.Should().BeTrue();
        messageFirst.CanGoForward.Should().BeFalse();

        var continuationFirst = new DictionaryPopupNavigationStateCoordinator();
        continuationFirst.CommitRoot(7, 30);
        continuationFirst.SetVisibility(true);
        continuationFirst.TryUpdate(7, 30, canGoBack: true, canGoForward: false)
            .Should().BeTrue();

        continuationFirst.IsVisible.Should().BeTrue();
        continuationFirst.CanGoBack.Should().BeTrue();
        continuationFirst.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void RootReplacementCommit_ResetsHistoryAndChangesExactIdentity()
    {
        var navigation = new DictionaryPopupNavigationStateCoordinator();
        navigation.CommitRoot(8, 40);
        navigation.TryUpdate(8, 40, true, true).Should().BeTrue();

        navigation.CommitRoot(8, 41);

        navigation.CanGoBack.Should().BeFalse();
        navigation.CanGoForward.Should().BeFalse();
        navigation.IsCurrent(8, 40).Should().BeFalse();
        navigation.IsCurrent(8, 41).Should().BeTrue();
    }
}
