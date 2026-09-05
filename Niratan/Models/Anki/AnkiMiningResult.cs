using System.Collections.Generic;
using System.Linq;

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
    IReadOnlyList<long>? NoteIds = null)
{
    /// <summary>
    /// Every note the magnifier may open for this entry. A duplicate can match more than one
    /// existing note, and collapsing them to a single id is what limits the jump to one card.
    /// </summary>
    public IReadOnlyList<long> OpenableNoteIds => NoteIds ?? [];

    /// <summary>First openable note, for single-note feedback and logging.</summary>
    public long? NoteId => OpenableNoteIds.Count > 0 ? OpenableNoteIds[0] : null;

    public string WebStatus => Status switch
    {
        AnkiMiningStatus.Added => "added",
        AnkiMiningStatus.Duplicate => "duplicate",
        AnkiMiningStatus.Pending => "pending",
        _ => "failed",
    };

    public static AnkiMiningResult Added(long noteId, string message = "Added to Anki.") =>
        new(AnkiMiningStatus.Added, message, Normalize([noteId]));

    public static AnkiMiningResult Duplicate(
        string message = "Already exists in Anki.",
        IReadOnlyList<long>? noteIds = null) =>
        new(AnkiMiningStatus.Duplicate, message, Normalize(noteIds));

    public static AnkiMiningResult Failed(string message) =>
        new(AnkiMiningStatus.Failed, message);

    public static AnkiMiningResult Pending(string message = "Preparing card…") =>
        new(AnkiMiningStatus.Pending, message);

    private static IReadOnlyList<long> Normalize(IReadOnlyList<long>? noteIds) =>
        noteIds is null
            ? []
            : noteIds.Where(noteId => noteId > 0).Distinct().ToArray();
}
