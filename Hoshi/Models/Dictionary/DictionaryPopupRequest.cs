using System.Collections.Generic;
using Hoshi.Enums;
using Hoshi.Models.Anki;
using Hoshi.Models.Settings;

namespace Hoshi.Models.Dictionary;

public sealed record DictionaryPopupRequest(
    string Query,
    List<DictionaryLookupResult> Results,
    Dictionary<string, string> Styles,
    DictionaryDisplaySettings DisplaySettings,
    ThemeMode Theme,
    AudioSettings AudioSettings,
    AnkiSettings AnkiSettings,
    AnkiMiningContext? MiningContext,
    string? TraceId);
