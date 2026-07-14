namespace Niratan.Services.Anki;

public sealed record AnkiMiningMediaNeeds(
    bool NeedsVideoScreenshot,
    bool NeedsVideoAudioClip,
    bool NeedsSasayakiAudio)
{
    public bool NeedsVideoMedia => NeedsVideoScreenshot || NeedsVideoAudioClip;
    public bool NeedsDirectMedia => NeedsVideoMedia || NeedsSasayakiAudio;
}

public sealed record AnkiMiningPreflightResult(
    bool CanMine,
    bool IsDuplicate,
    string? ErrorMessage,
    AnkiMiningMediaNeeds MediaNeeds,
    string? DirectMediaDirectory = null)
{
    public static AnkiMiningPreflightResult Failure(string message) =>
        new(false, false, message, new AnkiMiningMediaNeeds(false, false, false));

    public static AnkiMiningPreflightResult Duplicate() =>
        new(false, true, null, new AnkiMiningMediaNeeds(false, false, false));
}
