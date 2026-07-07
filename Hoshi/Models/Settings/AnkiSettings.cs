using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hoshi.Models.Settings;

public enum AnkiDuplicateScope
{
    Collection,
    Deck,
    DeckRoot,
}

public sealed class AnkiDeck
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    public static long ComputeStableId(string name)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"deck:{name}"));
        long id = 0;
        for (var i = 0; i < 8 && i < bytes.Length; i++)
            id = (id << 8) | bytes[i];
        return id & long.MaxValue;
    }
}

public sealed class AnkiNoteType
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public List<string> Fields { get; set; } = [];
}

public sealed class AnkiPopupSettings
{
    public bool IsConfigured { get; set; }
    public bool UseAnkiConnect { get; set; }
    public bool NeedsAudio { get; set; }
    public bool AllowDupes { get; set; }
    public bool CompactGlossaries { get; set; }
    public bool EmbedMedia { get; set; }
}

public sealed class AnkiSettings
{
    public string AnkiConnectUrl { get; set; } = "http://localhost:8765";
    public bool AnkiConnectForceSync { get; set; }

    public long? SelectedDeckId { get; set; }
    public string? SelectedDeckName { get; set; }
    public long? SelectedNoteTypeId { get; set; }
    public string? SelectedNoteTypeName { get; set; }

    public List<AnkiDeck> AvailableDecks { get; set; } = [];
    public List<AnkiNoteType> AvailableNoteTypes { get; set; } = [];

    public Dictionary<string, string> FieldMappings { get; set; } = new();

    public string Tags { get; set; } = "";

    public bool AllowDupes { get; set; }
    public bool CheckDuplicatesAcrossAllModels { get; set; }
    public AnkiDuplicateScope DuplicateScope { get; set; } = AnkiDuplicateScope.Collection;

    public bool CompactGlossaries { get; set; }
    public bool EmbedMedia { get; set; } = true;

    [JsonIgnore]
    public AnkiPopupSettings PopupSettings => new()
    {
        IsConfigured = !string.IsNullOrWhiteSpace(AnkiConnectUrl)
                       && SelectedDeckId.HasValue
                       && SelectedNoteTypeId.HasValue,
        UseAnkiConnect = !string.IsNullOrWhiteSpace(AnkiConnectUrl),
        NeedsAudio = FieldMappings.Values.Any(static template =>
            template?.Contains("{audio}", System.StringComparison.OrdinalIgnoreCase) == true),
        AllowDupes = AllowDupes,
        CompactGlossaries = CompactGlossaries,
        EmbedMedia = !string.IsNullOrWhiteSpace(AnkiConnectUrl),
    };

    [JsonIgnore]
    public bool IsConfigured => PopupSettings.IsConfigured;
}
