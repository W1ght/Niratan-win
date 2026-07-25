namespace Niratan.Services.Video;

/// <summary>
/// Tracks whether video playback was paused by subtitle lookup and should be
/// resumed when the lookup popup stack closes.
/// </summary>
public sealed class VideoLookupPlaybackCoordinator
{
    private bool _resumeOnDismiss;

    public bool TryPauseForLookup(bool isPlaying)
    {
        if (!isPlaying)
            return false;

        _resumeOnDismiss = true;
        return true;
    }

    public bool TryResumeAfterDismiss(bool isPlaying)
    {
        var shouldResume = _resumeOnDismiss && !isPlaying;
        _resumeOnDismiss = false;
        return shouldResume;
    }

    public void CancelAutoResume() => _resumeOnDismiss = false;
}
