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
    Task<bool> DuplicateCheckExpressionAsync(string expression);
    Task<bool> DuplicateCheckAsync(string rawPayloadJson);
    Task<string?> GetWritableMediaDirectoryAsync();
}
