using System.Threading.Tasks;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Audio;

public interface IAudioService
{
    Task PlayAsync(
        string url,
        AudioPlaybackMode mode,
        string? traceId = null,
        string? audioTraceId = null);
    void Stop();
    AudioSettings Settings { get; }
    void UpdateSettings(AudioSettings settings);
}
