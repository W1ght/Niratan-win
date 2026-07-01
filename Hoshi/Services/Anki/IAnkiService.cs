using System.Collections.Generic;
using System.Threading.Tasks;
using Hoshi.Models.Anki;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Anki;

public interface IAnkiService
{
    AnkiSettings Settings { get; }
    void UpdateSettings(AnkiSettings settings);

    Task<bool> IsAvailableAsync();
    Task<List<AnkiDeck>> FetchDecksAsync();
    Task<List<AnkiNoteType>> FetchNoteTypesAsync();
    Task<List<string>> FetchModelFieldNamesAsync(string modelName);
    Task<bool> MineEntryAsync(string rawPayloadJson, AnkiMiningContext context);
    Task<bool> DuplicateCheckAsync(string rawPayloadJson);
}
