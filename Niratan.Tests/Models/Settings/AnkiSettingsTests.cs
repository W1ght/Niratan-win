using FluentAssertions;
using Niratan.Models.Settings;

namespace Niratan.Tests.Models.Settings;

public class AnkiSettingsTests
{
    [Fact]
    public void PopupSettings_NeedsAudio_WhenAnyFieldMappingUsesAudioPlaceholder()
    {
        var settings = new AnkiSettings
        {
            FieldMappings =
            {
                ["Expression"] = "{expression}",
                ["ExpressionAudio"] = "{audio}",
            },
        };

        settings.PopupSettings.NeedsAudio.Should().BeTrue();
    }

    [Fact]
    public void PopupSettings_NeedsAudio_WhenAudioPlaceholderIsEmbeddedInFieldTemplate()
    {
        var settings = new AnkiSettings
        {
            FieldMappings =
            {
                ["Back"] = "Audio: {audio}",
            },
        };

        settings.PopupSettings.NeedsAudio.Should().BeTrue();
    }
}
