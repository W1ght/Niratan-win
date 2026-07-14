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
}
