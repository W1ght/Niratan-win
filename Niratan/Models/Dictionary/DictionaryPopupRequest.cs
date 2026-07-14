using System.Collections.Generic;
using Niratan.Enums;
using Niratan.Models.Anki;
using Niratan.Models.Settings;

namespace Niratan.Models.Dictionary;

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
