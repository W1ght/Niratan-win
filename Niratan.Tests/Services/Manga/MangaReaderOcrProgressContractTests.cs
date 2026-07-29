using FluentAssertions;

namespace Niratan.Tests.Services.Manga;

public sealed class MangaReaderOcrProgressContractTests
{
    private static readonly string ProjectRoot = ResolveProjectRoot();

    private static string ResolveProjectRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(
            "NIRATAN_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var configuredProject = Path.Combine(configuredRoot, "Niratan");
            if (Directory.Exists(configuredProject))
                return Path.GetFullPath(configuredProject);
        }
        var workingTree = Path.GetFullPath(
            Path.Combine(Directory.GetCurrentDirectory(), "Niratan"));
        return Directory.Exists(workingTree)
            ? workingTree
            : Path.GetFullPath(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "..",
                    "Niratan"));
    }

    [Fact]
    public void OcrScan_ReturnsCommandImmediatelyAndPublishesEachCompletedPage()
    {
        var viewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "MangaReaderViewModel.cs"));

        viewModel.Should().Contain("_ocrScanTask = RunOcrScanAsync");
        viewModel.Should().Contain("return Task.CompletedTask;");
        viewModel.IndexOf("ApplyTextRegions(pageIndex, regions)", StringComparison.Ordinal)
            .Should().BeLessThan(
                viewModel.IndexOf("OcrCompletedPageCount++", StringComparison.Ordinal));
        viewModel.Should().Contain("IsOcrRecognitionPaused = true");
        viewModel.Should().Contain("ResumeOcrRecognitionAsync");
    }

    [Fact]
    public void OcrScan_ReopenRestoresCacheBeforeResumingMissingPages()
    {
        var viewModel = File.ReadAllText(
            Path.Combine(
                ProjectRoot,
                "ViewModels",
                "Pages",
                "MangaReaderViewModel.cs"));

        viewModel.Should().Contain(
            "if (IsGoogleOcrEnabled && GoogleOcrDisclosureAccepted)");
        viewModel.Should().Contain("StartOcrRecognition();");

        var scanStart = viewModel.IndexOf(
            "private async Task RunOcrScanAsync",
            StringComparison.Ordinal);
        var scanEnd = viewModel.IndexOf(
            "[RelayCommand(CanExecute",
            scanStart,
            StringComparison.Ordinal);
        var scan = viewModel[scanStart..scanEnd];
        scan.IndexOf("GetCachedRegionsAsync(", StringComparison.Ordinal)
            .Should().BeLessThan(
                scan.IndexOf("GetPagePathAsync(", StringComparison.Ordinal));
        scan.Should().Contain("if (cached is not null)");
        scan.Should().Contain("requestedNetworkPage = true");
    }
}
