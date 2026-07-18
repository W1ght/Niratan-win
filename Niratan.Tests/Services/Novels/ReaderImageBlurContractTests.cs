using FluentAssertions;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderImageBlurContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    private static string ReadProjectFile(params string[] parts) =>
        File.ReadAllText(Path.Combine([ProjectRoot, .. parts]));

    [Fact]
    public void AppearanceAndReaderBridge_ExposeNiratanAlignedImageBlur()
    {
        var settings = ReadProjectFile("Models", "Settings", "ReaderSettings.cs");
        var viewModel = ReadProjectFile("ViewModels", "Pages", "SettingsPageViewModel.cs");
        var appearance = ReadProjectFile("Views", "Controls", "ReaderAppearanceSettingsContent.xaml");
        var readerPage = ReadProjectFile("Views", "Pages", "NovelReaderPage.xaml.cs");
        var bridge = ReadProjectFile("Web", "NovelReader", "reader-bridge.js");

        settings.Should().Contain("public bool BlurImages { get; set; } = false;");
        viewModel.Should().Contain("public partial bool BlurImages { get; set; }");
        viewModel.Should().Contain("ApplyReaderSetting(s => s.BlurImages, value)");
        appearance.Should().Contain("x:Uid=\"ReaderBlurImagesCard\"");
        appearance.Should().Contain("ViewModel.BlurImages, Mode=TwoWay");
        readerPage.Should().Contain("window.__niratanBlurImages");
        readerPage.Should().Contain("case \"imageTapped\":");
        readerPage.Should().Contain("ReaderWebContentPolicy.IsAllowedTopLevelNavigation(source)");

        bridge.Should().Contain("window.__niratanBlurImages === true");
        bridge.Should().Contain("blurredTarget.classList.add(\"niratan-blurred\")");
        bridge.Should().Contain("blurredTarget.classList.remove(\"niratan-blurred\")");
        bridge.Should().Contain("postToHost(\"imageTapped\"");
        bridge.Should().Contain("img.classList.contains(\"gaiji\")");
        bridge.Should().Contain("img.classList.contains(\"gaiji-line\")");
    }
}
