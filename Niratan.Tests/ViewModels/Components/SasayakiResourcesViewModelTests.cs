using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Models.Sasayaki;
using Niratan.Models.Settings;
using Niratan.Services.Sasayaki;
using Niratan.Services.Settings;
using Niratan.Services.UI;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.ViewModels.Components;

public sealed class SasayakiResourcesViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsExistingResourcesAndMatchSummary()
    {
        var sidecar = new Mock<ISasayakiSidecarService>();
        var book = Book();
        sidecar.Setup(service => service.LoadSourceAsync(
                book.ExtractedPath!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SasayakiSourceData
            {
                AudiobookPath = "D:\\Audio\\book.m4b",
                SrtPath = "D:\\Audio\\book.srt",
            });
        sidecar.Setup(service => service.LoadMatchAsync(
                book.ExtractedPath!,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SasayakiMatchData
            {
                Matches = [new SasayakiMatch { Id = "1", Text = "星" }],
                Unmatched = 1,
            });
        var sut = CreateSut(sidecarService: sidecar.Object, searchWindow: 321);

        await sut.InitializeAsync(book);

        sut.AudiobookDisplayName.Should().Be("book.m4b");
        sut.SubtitleDisplayName.Should().Be("book.srt");
        sut.CurrentMatchSummary.Should().Contain("1/2").And.Contain("50.0");
        sut.SearchWindowSizeValue.Should().Be(321);
        sut.CanMatch.Should().BeTrue();
    }

    [Fact]
    public async Task MatchCommand_UsesFilesSelectedInsideResourcesPanelAndUpdatesSummary()
    {
        var dialog = new Mock<IDialogService>();
        dialog.SetupSequence(service => service.OpenFilePickerAsync(It.IsAny<string[]>()))
            .ReturnsAsync("D:\\Audio\\book.m4b")
            .ReturnsAsync("D:\\Audio\\book.srt");
        var match = new Mock<ISasayakiMatchService>();
        var book = Book();
        match.Setup(service => service.MatchAsync(
                book,
                "D:\\Audio\\book.m4b",
                "D:\\Audio\\book.srt",
                400,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SasayakiMatchData
            {
                Matches = [new SasayakiMatch { Id = "1", Text = "星" }],
            });
        var sut = CreateSut(
            dialogService: dialog.Object,
            matchService: match.Object,
            searchWindow: 400);
        await sut.InitializeAsync(book);

        await sut.PickAudiobookCommand.ExecuteAsync(null);
        await sut.PickSubtitleCommand.ExecuteAsync(null);
        await sut.MatchCommand.ExecuteAsync(null);

        match.VerifyAll();
        sut.CurrentMatchSummary.Should().Contain("1/1");
        sut.IsMatching.Should().BeFalse();
        sut.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task MatchCommand_WhenMatchingFails_ShowsInlineError()
    {
        var match = new Mock<ISasayakiMatchService>();
        var book = Book();
        match.Setup(service => service.MatchAsync(
                book,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("subtitle is invalid"));
        var sut = CreateSut(matchService: match.Object);
        await sut.InitializeAsync(book);
        sut.AudiobookPath = "D:\\Audio\\book.m4b";
        sut.SubtitlePath = "D:\\Audio\\book.srt";

        await sut.MatchCommand.ExecuteAsync(null);

        sut.ErrorMessage.Should().Be("subtitle is invalid");
        sut.HasError.Should().BeTrue();
        sut.IsMatching.Should().BeFalse();
    }

    private static SasayakiResourcesViewModel CreateSut(
        IDialogService? dialogService = null,
        ISasayakiMatchService? matchService = null,
        ISasayakiSidecarService? sidecarService = null,
        int searchWindow = 2000)
    {
        var sidecar = sidecarService ?? EmptySidecar();
        var settings = Mock.Of<ISettingsService>(service => service.Current == new AppSettings
        {
            SasayakiSettings = new SasayakiSettings { SearchWindowSize = searchWindow },
        });
        return new SasayakiResourcesViewModel(
            dialogService ?? Mock.Of<IDialogService>(),
            matchService ?? Mock.Of<ISasayakiMatchService>(),
            sidecar,
            settings);
    }

    private static ISasayakiSidecarService EmptySidecar()
    {
        var sidecar = new Mock<ISasayakiSidecarService>();
        sidecar.Setup(service => service.LoadSourceAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SasayakiSourceData?)null);
        sidecar.Setup(service => service.LoadMatchAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((SasayakiMatchData?)null);
        return sidecar.Object;
    }

    private static NovelBook Book() => new()
    {
        Id = "book-1",
        Title = "Book One",
        FilePath = "D:\\Books\\book.epub",
        ExtractedPath = "D:\\Books\\book-1",
    };
}
