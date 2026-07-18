using System.Collections.Generic;
using System.Threading.Tasks;
using Niratan.Models.Anki;
using Niratan.Models.Settings;

namespace Niratan.Services.Anki;

public interface IAnkiService
{
    AnkiSettings Settings { get; }
    void UpdateSettings(AnkiSettings settings);

    Task<bool> IsAvailableAsync();
    Task<List<AnkiDeck>> FetchDecksAsync();
    Task<List<AnkiNoteType>> FetchNoteTypesAsync();
    Task<List<string>> FetchModelFieldNamesAsync(string modelName);
    Task<AnkiMiningPreflightResult> PreflightMiningAsync(string rawPayloadJson, AnkiMiningContext context);
    Task<long?> MineEntryAsync(string rawPayloadJson, AnkiMiningContext context);
    Task<bool> OpenNoteInAnkiAsync(long noteId);
    async Task<bool> OpenNotesInAnkiAsync(IReadOnlyList<long> noteIds) =>
        noteIds.Count > 0 && await OpenNoteInAnkiAsync(noteIds[0]);
    async Task<AnkiDuplicateLookupResult> DuplicateLookupExpressionAsync(string expression) =>
        await DuplicateCheckExpressionAsync(expression)
            ? AnkiDuplicateLookupResult.Duplicate()
            : AnkiDuplicateLookupResult.NotDuplicate();
    Task<bool> DuplicateCheckExpressionAsync(string expression);
    Task<bool> DuplicateCheckAsync(string rawPayloadJson);
    Task<string?> GetWritableMediaDirectoryAsync();
}
