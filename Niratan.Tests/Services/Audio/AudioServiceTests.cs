using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Audio;

namespace Niratan.Tests.Services.Audio;

public class AudioServiceTests
{
    [Fact]
    public void UpdateSettings_StoresAndReturnsSettings()
    {
        using var service = new AudioService();
        var settings = new AudioSettings
        {
            EnableAutoplay = true,
            PlaybackMode = AudioPlaybackMode.Duck,
        };

        service.UpdateSettings(settings);

        service.Settings.Should().BeSameAs(settings);
        service.Settings.EnableAutoplay.Should().BeTrue();
        service.Settings.PlaybackMode.Should().Be(AudioPlaybackMode.Duck);
    }

    [Fact]
    public void Stop_OnNewService_DoesNotThrow()
    {
        using var service = new AudioService();
        var act = () => service.Stop();
        act.Should().NotThrow();
    }

    [Fact]
    public void Stop_CalledTwice_DoesNotThrow()
    {
        using var service = new AudioService();
        service.Stop();
        var act = () => service.Stop();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task PlayAsync_EmptyUrl_LogsWarningAndReturns()
    {
        using var service = new AudioService();
        // No dispatcher in tests, so it should log error and return early
        var act = () => service.PlayAsync("", AudioPlaybackMode.Interrupt);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PlayAsync_InvalidUrl_DoesNotCrash()
    {
        using var service = new AudioService();
        // Should not crash even with completely invalid URL
        var act = () => service.PlayAsync("not-a-valid-url", AudioPlaybackMode.Interrupt);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void PlaybackMode_Serialization_RoundTrips()
    {
        var settings = new AudioSettings { PlaybackMode = AudioPlaybackMode.Mix };
        settings.PlaybackModeText.Should().Be("mix");
        settings.PlaybackMode = AudioPlaybackMode.Duck;
        settings.PlaybackModeText.Should().Be("duck");
        settings.PlaybackMode = AudioPlaybackMode.Interrupt;
        settings.PlaybackModeText.Should().Be("interrupt");
    }

    [Fact]
    public void EnabledAudioSourceUrls_WithDefaultSettings_ReturnsDefault()
    {
        var settings = new AudioSettings();
        var urls = settings.EnabledAudioSourceUrls;

        urls.Should().ContainSingle()
            .Which.Should().Contain("hoshi-reader.manhhaoo-do.workers.dev");
    }

    [Fact]
    public void EnabledAudioSourceUrls_WithLocalAudio_ReturnsLocalFirst()
    {
        var settings = new AudioSettings { EnableLocalAudio = true };
        var urls = settings.EnabledAudioSourceUrls;

        urls.Should().HaveCount(2);
        urls[0].Should().Contain("localhost:18765");
    }

    [Fact]
    public void EnabledAudioSourceUrls_WithDisabledSource_SkipsIt()
    {
        var settings = new AudioSettings();
        settings.AudioSources.Add(new AudioSource
        {
            Name = "Custom",
            Url = "https://example.com/audio",
            IsEnabled = false,
        });

        var urls = settings.EnabledAudioSourceUrls;
        urls.Should().HaveCount(1); // only the default is enabled
    }

    [Fact]
    public void UnplayedService_Dispose_DoesNotThrow()
    {
        var service = new AudioService();
        var act = () => service.Dispose();
        act.Should().NotThrow();
    }
}
