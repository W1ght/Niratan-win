using System.Collections.Generic;

namespace Niratan.Models.Anki;

public sealed record AnkiDuplicateLookupResult(
    bool IsDuplicate,
    IReadOnlyList<long> NoteIds)
{
    public static AnkiDuplicateLookupResult NotDuplicate() => new(false, []);

    public static AnkiDuplicateLookupResult Duplicate(IReadOnlyList<long>? noteIds = null) =>
        new(true, noteIds ?? []);
}
