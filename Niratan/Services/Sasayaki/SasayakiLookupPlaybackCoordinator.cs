namespace Niratan.Services.Sasayaki;

/// <summary>
/// Tracks whether Sasayaki playback was paused by a lookup popup and therefore
/// should be resumed when the popup stack closes.
/// </summary>
public sealed class SasayakiLookupPlaybackCoordinator
{
    private bool _resumeOnDismiss;

    public bool TryPauseForLookup(bool autoPauseEnabled, bool isPlaying)
    {
        if (!autoPauseEnabled || !isPlaying)
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
