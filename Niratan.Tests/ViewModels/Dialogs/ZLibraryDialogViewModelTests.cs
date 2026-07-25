using CommunityToolkit.Mvvm.Messaging;
using FluentAssertions;
using Moq;
using Niratan.Services.UI;
using Niratan.Services.ZLibrary;
using Niratan.ViewModels.Dialogs;

namespace Niratan.Tests.ViewModels.Dialogs;

public sealed class ZLibraryDialogViewModelTests
{
    [Fact]
    public void SearchDefaultsToEpubBooksApiFilters()
    {
        using var sut = CreateViewModel();

        sut.SelectedExtension.Value.Should().Be("EPUB");
        sut.ExactMatching.Should().BeFalse();
    }

    private static ZLibraryDialogViewModel CreateViewModel() => new(
        Mock.Of<IZLibraryService>(),
        Mock.Of<INotificationService>(),
        WeakReferenceMessenger.Default);
}
