using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public sealed class ReaderSettingsTests
{
    [Fact]
    public void Defaults_UseKleeAsReaderFont()
    {
        var settings = new ReaderSettings();

        settings.SelectedFont.Should().Be("'Klee One', 'Yu Mincho', serif");
    }

    [Fact]
    public void Defaults_EnableAllReaderDisplayItems()
    {
        var settings = new ReaderSettings();

        settings.ShowTitle.Should().BeTrue();
        settings.ShowCharacters.Should().BeTrue();
        settings.ShowPercentage.Should().BeTrue();
        settings.ShowStatisticsToggle.Should().BeTrue();
        settings.ShowReadingSpeed.Should().BeTrue();
        settings.ShowReadingTime.Should().BeTrue();
    }
}
