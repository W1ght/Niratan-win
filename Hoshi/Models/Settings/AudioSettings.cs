using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hoshi.Models.Settings;

public enum AudioPlaybackMode
{
    Interrupt,
    Duck,
    Mix,
}

public sealed class AudioSource
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; } = false;
}

public sealed class AudioSettings
{
    public const string DefaultAudioUrl = "https://hoshi-reader.manhhaoo-do.workers.dev/?term={term}&reading={reading}";
    public const string LocalAudioUrl = "http://localhost:18765/localaudio/get/?term={term}&reading={reading}";
    public const string LegacyLocalAudioUrl = "http://localhost:8765/localaudio/get/?term={term}&reading={reading}";

    public List<AudioSource> AudioSources { get; set; } = new()
    {
        new AudioSource { Name = "Default", Url = DefaultAudioUrl, IsEnabled = true, IsDefault = true },
    };

    public bool EnableLocalAudio { get; set; } = false;
    public bool EnableAutoplay { get; set; } = false;
    public AudioPlaybackMode PlaybackMode { get; set; } = AudioPlaybackMode.Interrupt;

    [JsonIgnore]
    public List<string> EnabledAudioSourceUrls
    {
        get
        {
            var sources = NormalizedSources();
            return sources.Where(s => s.IsEnabled).Select(s => s.Url).ToList();
        }
    }

    public string PlaybackModeText => PlaybackMode switch
    {
        AudioPlaybackMode.Interrupt => "interrupt",
        AudioPlaybackMode.Duck => "duck",
        AudioPlaybackMode.Mix => "mix",
        _ => "interrupt",
    };

    public List<AudioSource> NormalizedSources()
    {
        var sources = new List<AudioSource>(AudioSources);

        // Remove local source entries that might be stale
        sources.RemoveAll(s => s.Url == LocalAudioUrl || s.Url == LegacyLocalAudioUrl);

        if (EnableLocalAudio)
        {
            sources.Insert(0, new AudioSource
            {
                Name = "Local",
                Url = LocalAudioUrl,
                IsEnabled = true,
            });
        }

        if (sources.Count == 0)
        {
            sources.Add(new AudioSource { Name = "Default", Url = DefaultAudioUrl, IsEnabled = true, IsDefault = true });
        }

        return sources;
    }

    public void AddSource(AudioSource source)
    {
        if (string.IsNullOrWhiteSpace(source.Url) || string.IsNullOrWhiteSpace(source.Name))
            return;
        if (AudioSources.Exists(s => s.Url == source.Url))
            return;
        AudioSources.Add(source);
    }

    public void SetLocalAudioEnabled(bool enabled)
    {
        EnableLocalAudio = enabled;
    }
}
