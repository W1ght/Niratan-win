using FluentAssertions;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.ViewModels.Components;

public sealed class MangaBrowseSourceItemViewModelTests
{
    [Fact]
    public async Task RemoveCommand_IsAvailableWhenSourceIsRemovable()
    {
        var removed = false;
        var item = new MangaBrowseSourceItemViewModel(
            "XCOMIC",
            "zh",
            "Mihon APK",
            () => Task.CompletedTask,
            remove: () =>
            {
                removed = true;
                return Task.CompletedTask;
            });

        item.CanRemove.Should().BeTrue();
        item.RemoveCommand.CanExecute(null).Should().BeTrue();
        await item.RemoveCommand.ExecuteAsync(null);
        removed.Should().BeTrue();
        item.RemoveAutomationId.Should().StartWith("BrowseSourceRemove_");
    }

    [Fact]
    public void RemoveCommand_IsHiddenForNonMihonSource()
    {
        var item = new MangaBrowseSourceItemViewModel(
            "Suwayomi",
            "en",
            "Suwayomi",
            () => Task.CompletedTask);

        item.CanRemove.Should().BeFalse();
        item.RemoveCommand.CanExecute(null).Should().BeFalse();
    }
}
