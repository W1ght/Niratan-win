using FluentAssertions;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class GamesPageViewModelThreadingContractTests
{
    private static readonly string ViewModelPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "Niratan",
            "ViewModels",
            "Pages",
            "GamesPageViewModel.cs"));

    [Fact]
    public void SessionStateChangesAreMarshaledToTheUiDispatcher()
    {
        var source = File.ReadAllText(ViewModelPath);
        var handlerStart = source.IndexOf(
            "private void Session_StateChanged",
            StringComparison.Ordinal);
        var applyStart = source.IndexOf(
            "private void ApplySessionState",
            handlerStart,
            StringComparison.Ordinal);

        handlerStart.Should().BeGreaterThanOrEqualTo(0);
        applyStart.Should().BeGreaterThan(handlerStart);

        var handler = source[handlerStart..applyStart];
        source.Should().Contain("DispatcherQueue.GetForCurrentThread()");
        handler.Should().Contain("HasThreadAccess: false");
        handler.Should().Contain("dispatcher.TryEnqueue");
        handler.IndexOf("dispatcher.TryEnqueue", StringComparison.Ordinal)
            .Should().BeLessThan(handler.LastIndexOf("ApplySessionState(state)", StringComparison.Ordinal));
    }
}
