namespace Niratan.Services.Dictionary;

internal sealed class DictionaryPopupNavigationStateCoordinator
{
    private long? _documentEpoch;
    private long? _generation;

    public bool IsVisible { get; private set; }
    public bool CanGoBack { get; private set; }
    public bool CanGoForward { get; private set; }

    public void CommitRoot(long documentEpoch, long generation)
    {
        _documentEpoch = documentEpoch;
        _generation = generation;
        CanGoBack = false;
        CanGoForward = false;
    }

    public bool TryUpdate(
        long documentEpoch,
        long generation,
        bool canGoBack,
        bool canGoForward)
    {
        if (!IsCurrent(documentEpoch, generation))
            return false;

        CanGoBack = canGoBack;
        CanGoForward = canGoForward;
        return true;
    }

    public bool IsCurrent(long documentEpoch, long generation) =>
        _documentEpoch == documentEpoch && _generation == generation;

    public void SetVisibility(bool isVisible)
    {
        IsVisible = isVisible;
    }

    public void Reset()
    {
        _documentEpoch = null;
        _generation = null;
        IsVisible = false;
        CanGoBack = false;
        CanGoForward = false;
    }
}
