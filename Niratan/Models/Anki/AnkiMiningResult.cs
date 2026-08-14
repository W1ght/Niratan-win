namespace Niratan.Models.Anki;

public enum AnkiMiningStatus
{
    Added,
    Duplicate,
    Failed,
    Pending,
}

public sealed record AnkiMiningResult(
    AnkiMiningStatus Status,
    string Message,
    long? NoteId = null)
{
    public string WebStatus => Status switch
    {
        AnkiMiningStatus.Added => "added",
        AnkiMiningStatus.Duplicate => "duplicate",
        AnkiMiningStatus.Pending => "pending",
        _ => "failed",
    };

    public static AnkiMiningResult Added(long noteId, string message = "Added to Anki.") =>
        new(AnkiMiningStatus.Added, message, noteId);

    public static AnkiMiningResult Duplicate(
        string message = "Already exists in Anki.",
        long? noteId = null) =>
        new(AnkiMiningStatus.Duplicate, message, noteId);

    public static AnkiMiningResult Failed(string message) =>
        new(AnkiMiningStatus.Failed, message);

    public static AnkiMiningResult Pending(string message = "Preparing card…") =>
        new(AnkiMiningStatus.Pending, message);
}
