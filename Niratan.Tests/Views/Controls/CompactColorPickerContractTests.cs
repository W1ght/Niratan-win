using FluentAssertions;

namespace Niratan.Tests.Views.Controls;

public sealed class CompactColorPickerContractTests
{
    private static readonly string ProjectRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "Niratan"));

    [Fact]
    public void DefaultFlyoutPlacement_UsesSupportedExplicitValue()
    {
        var source = File.ReadAllText(
            Path.Combine(ProjectRoot, "Views", "Controls", "CompactColorPicker.xaml.cs"));

        source.Should().Contain("new PropertyMetadata(FlyoutPlacementMode.Bottom)");
        source.Should().NotContain("new PropertyMetadata(FlyoutPlacementMode.Auto)");
    }
}
