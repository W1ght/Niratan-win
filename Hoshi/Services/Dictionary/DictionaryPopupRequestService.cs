using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Anki;
using Hoshi.Models.Dictionary;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;
using Serilog;

namespace Hoshi.Services.Dictionary;

public sealed class DictionaryPopupRequestService : IDictionaryPopupRequestService
{
    private readonly IDictionaryLookupService _lookupService;
    private readonly ISettingsService _settingsService;

    public DictionaryPopupRequestService(
        IDictionaryLookupService lookupService,
        ISettingsService settingsService)
    {
        _lookupService = lookupService;
        _settingsService = settingsService;
    }

    public async Task<DictionaryPopupRequest?> CreateAsync(
        string query,
        AnkiMiningContext? miningContext = null,
        string? traceId = null,
        CancellationToken ct = default)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return null;

        traceId ??= $"popup-request-{Stopwatch.GetTimestamp():x}";
        var totalSw = Stopwatch.StartNew();
        Log.Information(
            "[LookupTrace] trace={TraceId} popup request start query='{Query}'",
            traceId, query);

        ct.ThrowIfCancellationRequested();

        var settings = _settingsService.Current;
        var displaySettings = CloneDisplaySettings(settings.DictionaryDisplaySettings);
        var lookupSw = Stopwatch.StartNew();
        var results = await _lookupService.LookupAsync(
            query,
            displaySettings.MaxResults,
            displaySettings.ScanLength,
            traceId);
        Log.Information(
            "[LookupTrace] trace={TraceId} popup request lookup finished in {Ms}ms total={TotalMs}ms results={Count}",
            traceId, lookupSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, results.Count);
        if (results.Count == 0)
            return null;

        ct.ThrowIfCancellationRequested();

        var stylesSw = Stopwatch.StartNew();
        var styles = (await _lookupService.GetStylesAsync())
            .ToDictionary(style => style.DictName, style => style.Styles);
        Log.Information(
            "[LookupTrace] trace={TraceId} popup request styles finished in {Ms}ms total={TotalMs}ms styles={StyleCount}",
            traceId, stylesSw.ElapsedMilliseconds, totalSw.ElapsedMilliseconds, styles.Count);

        Log.Information(
            "[LookupTrace] trace={TraceId} popup request finished in {TotalMs}ms",
            traceId, totalSw.ElapsedMilliseconds);
        return new DictionaryPopupRequest(
            query,
            results,
            styles,
            displaySettings,
            settings.Theme,
            CloneAudioSettings(settings.AudioSettings),
            CloneAnkiSettings(settings.AnkiSettings),
            miningContext,
            traceId);
    }

    private static DictionaryDisplaySettings CloneDisplaySettings(DictionaryDisplaySettings settings) =>
        settings with
        {
            CollapsedDictionaries = settings.CollapsedDictionaries is null
                ? null
                : new HashSet<string>(settings.CollapsedDictionaries),
        };

    private static AudioSettings CloneAudioSettings(AudioSettings settings) =>
        new()
        {
            AudioSources = settings.AudioSources
                .Select(source => new AudioSource
                {
                    Name = source.Name,
                    Url = source.Url,
                    IsEnabled = source.IsEnabled,
                    IsDefault = source.IsDefault,
                })
                .ToList(),
            EnableLocalAudio = settings.EnableLocalAudio,
            EnableAutoplay = settings.EnableAutoplay,
            PlaybackMode = settings.PlaybackMode,
        };

    private static AnkiSettings CloneAnkiSettings(AnkiSettings settings) =>
        new()
        {
            AnkiConnectUrl = settings.AnkiConnectUrl,
            AnkiConnectForceSync = settings.AnkiConnectForceSync,
            SelectedDeckId = settings.SelectedDeckId,
            SelectedDeckName = settings.SelectedDeckName,
            SelectedNoteTypeId = settings.SelectedNoteTypeId,
            SelectedNoteTypeName = settings.SelectedNoteTypeName,
            AvailableDecks = settings.AvailableDecks
                .Select(deck => new AnkiDeck
                {
                    Id = deck.Id,
                    Name = deck.Name,
                })
                .ToList(),
            AvailableNoteTypes = settings.AvailableNoteTypes
                .Select(noteType => new AnkiNoteType
                {
                    Id = noteType.Id,
                    Name = noteType.Name,
                    Fields = noteType.Fields.ToList(),
                })
                .ToList(),
            FieldMappings = new Dictionary<string, string>(settings.FieldMappings),
            Tags = settings.Tags,
            AllowDupes = settings.AllowDupes,
            CheckDuplicatesAcrossAllModels = settings.CheckDuplicatesAcrossAllModels,
            DuplicateScope = settings.DuplicateScope,
            CompactGlossaries = settings.CompactGlossaries,
            EmbedMedia = settings.EmbedMedia,
        };
}
