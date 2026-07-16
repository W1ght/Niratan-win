using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Niratan.Models.Anki;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.Settings;
using Serilog;

namespace Niratan.Services.Anki;

public sealed class AnkiService : IAnkiService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IDictionaryLookupService _dictionaryLookupService;
    private AnkiConnectClient? _client;
    private AnkiSettings _settings;
    private string? _cachedWritableMediaDirectory;
    private readonly ConcurrentDictionary<string, byte> _savedExpressions = new(StringComparer.Ordinal);

    public AnkiSettings Settings => _settings;

    public AnkiService(ISettingsService settingsService, IDictionaryLookupService dictionaryLookupService)
    {
        _settingsService = settingsService;
        _dictionaryLookupService = dictionaryLookupService;
        _settings = settingsService.Current.AnkiSettings;
    }

    public void UpdateSettings(AnkiSettings settings)
    {
        _settings = settings;
        _client?.Dispose();
        _client = null;
        _cachedWritableMediaDirectory = null;
    }

    private AnkiConnectClient GetClient()
    {
        if (_client == null)
        {
            var url = _settings.AnkiConnectUrl;
            if (string.IsNullOrWhiteSpace(url))
                url = "http://localhost:8765";
            _client = new AnkiConnectClient(url);
        }
        return _client;
    }

    public async Task<bool> IsAvailableAsync()
    {
        try
        {
            return await GetClient().IsAvailableAsync();
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<AnkiDeck>> FetchDecksAsync()
    {
        return await GetClient().FetchDecksAsync();
    }

    public async Task<List<AnkiNoteType>> FetchNoteTypesAsync()
    {
        return await GetClient().FetchNoteTypesAsync();
    }

    public async Task<List<string>> FetchModelFieldNamesAsync(string modelName)
    {
        return await GetClient().FetchModelFieldNamesAsync(modelName);
    }

    public async Task<AnkiMiningPreflightResult> PreflightMiningAsync(
        string rawPayloadJson,
        AnkiMiningContext context)
    {
        try
        {
            if (!_settings.IsConfigured)
                return AnkiMiningPreflightResult.Failure("Configure Anki deck and model first.");

            var payload = AnkiMiningPayload.FromJson(rawPayloadJson);
            var deck = ResolveDeck();
            var noteType = ResolveNoteType();
            if (deck == null || noteType == null)
                return AnkiMiningPreflightResult.Failure("Configure Anki deck and model first.");

            var renderedFields = RenderFieldsForDuplicateCheck(noteType, payload, context);
            if (renderedFields.Count == 0)
                return AnkiMiningPreflightResult.Failure("No Anki fields rendered.");

            if (!_settings.AllowDupes)
            {
                if (await DuplicateCheckExpressionAsync(payload.Expression))
                    return AnkiMiningPreflightResult.Duplicate();
            }

            var needs = AnkiFieldMappingResolver.ResolveMediaNeedsForMining(
                noteType,
                _settings.FieldMappings,
                context);
            var directMediaDirectory = needs.NeedsDirectMedia
                ? await GetWritableMediaDirectoryAsync()
                : null;
            return new AnkiMiningPreflightResult(true, false, null, needs, directMediaDirectory);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Anki] PreflightMiningAsync failed");
            return AnkiMiningPreflightResult.Failure(ex.Message);
        }
    }

    public async Task<long?> MineEntryAsync(string rawPayloadJson, AnkiMiningContext context)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!_settings.IsConfigured)
            {
                Log.Warning("[Anki] Not configured");
                return null;
            }

            var payload = AnkiMiningPayload.FromJson(rawPayloadJson);

            // Resolve deck
            var deck = ResolveDeck();
            if (deck == null)
            {
                Log.Warning("[Anki] Deck not found (id={DeckId}, name={DeckName})",
                    _settings.SelectedDeckId, _settings.SelectedDeckName);
                return null;
            }

            // Resolve note type
            var noteType = ResolveNoteType();
            if (noteType == null)
            {
                Log.Warning("[Anki] Note type not found (id={NoteTypeId}, name={NoteTypeName})",
                    _settings.SelectedNoteTypeId, _settings.SelectedNoteTypeName);
                return null;
            }

            var client = GetClient();

            // --- Phase 1: Resolve/download remote audio (separate HTTP, not AnkiConnect) ---
            var audioSw = System.Diagnostics.Stopwatch.StartNew();
            AnkiAudioDownloadResult? remoteAudio = null;
            if (!string.IsNullOrWhiteSpace(payload.Audio))
            {
                try
                {
                    remoteAudio = await s_audioDownloader.DownloadAsync(payload.Audio);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Anki] Failed to resolve/download audio");
                }
            }
            audioSw.Stop();
            Log.Information("[Anki] audioResolve/download completed in {ElapsedMs}ms hasAudio={HasAudio}",
                audioSw.ElapsedMilliseconds, remoteAudio != null);

            // --- Phase 2: Collect all media for batched upload ---
            var mediaReadSw = System.Diagnostics.Stopwatch.StartNew();
            var uploads = new List<(string filename, byte[] data)>();
            // Track which upload indices correspond to what
            int? audioUploadIdx = null;
            int? coverUploadIdx = null;
            int? sasayakiAudioUploadIdx = null;
            int? videoScreenshotUploadIdx = null;
            int? videoAudioClipUploadIdx = null;
            var dictMediaIndices = new List<(int idx, string originalFilename)>();

            if (!string.IsNullOrWhiteSpace(context.CoverPath) && File.Exists(context.CoverPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(context.CoverPath);
                    coverUploadIdx = uploads.Count;
                    uploads.Add((Path.GetFileName(context.CoverPath), bytes));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Anki] Failed to read cover image");
                }
            }

            if (!string.IsNullOrWhiteSpace(context.SasayakiAudioPath) && File.Exists(context.SasayakiAudioPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(context.SasayakiAudioPath);
                    sasayakiAudioUploadIdx = uploads.Count;
                    uploads.Add((Path.GetFileName(context.SasayakiAudioPath), bytes));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Anki] Failed to read sasayaki audio");
                }
            }

            if (!string.IsNullOrWhiteSpace(context.VideoScreenshotPath) && File.Exists(context.VideoScreenshotPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(context.VideoScreenshotPath);
                    videoScreenshotUploadIdx = uploads.Count;
                    uploads.Add((Path.GetFileName(context.VideoScreenshotPath), bytes));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Anki] Failed to read video screenshot");
                }
            }

            if (!string.IsNullOrWhiteSpace(context.VideoAudioClipPath) && File.Exists(context.VideoAudioClipPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(context.VideoAudioClipPath);
                    videoAudioClipUploadIdx = uploads.Count;
                    uploads.Add((Path.GetFileName(context.VideoAudioClipPath), bytes));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Anki] Failed to read video audio clip");
                }
            }

            if (remoteAudio != null)
            {
                audioUploadIdx = uploads.Count;
                uploads.Add((remoteAudio.Filename, remoteAudio.Bytes));
            }

            if (_settings.EmbedMedia)
            {
                foreach (var media in payload.DictionaryMediaList)
                {
                    try
                    {
                        var mediaBytes = await ResolveDictionaryMediaAsync(media);
                        if (mediaBytes != null)
                        {
                            dictMediaIndices.Add((uploads.Count, media.Filename));
                            uploads.Add((media.Filename, mediaBytes));
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[Anki] Failed to read dictionary media {Filename}", media.Filename);
                    }
                }
            }
            mediaReadSw.Stop();
            Log.Information("[Anki] mediaRead completed in {ElapsedMs}ms uploadCount={UploadCount}",
                mediaReadSw.ElapsedMilliseconds, uploads.Count);

            // --- Phase 3: Batch upload all media in one request ---
            var mediaUploadSw = System.Diagnostics.Stopwatch.StartNew();
            List<string> storedNames = [];
            if (uploads.Count > 0)
            {
                try
                {
                    storedNames = await client.StoreMediaFilesAsync(uploads);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Anki] Batch media upload failed ({Count} files)", uploads.Count);
                    storedNames = [];
                }
            }
            mediaUploadSw.Stop();
            Log.Information("[Anki] mediaUpload completed in {ElapsedMs}ms uploadCount={UploadCount}",
                mediaUploadSw.ElapsedMilliseconds, uploads.Count);

            if (coverUploadIdx is int coverIdx
                && coverIdx < storedNames.Count
                && !string.IsNullOrWhiteSpace(storedNames[coverIdx]))
            {
                context.CoverTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[coverIdx]);
            }

            if (videoScreenshotUploadIdx is int screenshotIdx && screenshotIdx < storedNames.Count)
                context.VideoScreenshotTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[screenshotIdx]);

            if (videoAudioClipUploadIdx is int videoAudioIdx && videoAudioIdx < storedNames.Count)
                context.VideoAudioClipTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[videoAudioIdx]);

            if (sasayakiAudioUploadIdx is int sasayakiAudioIdx && sasayakiAudioIdx < storedNames.Count)
                context.SasayakiAudioTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[sasayakiAudioIdx]);

            // --- Phase 4: Build mediaPayload and dictionaryMediaTags from upload results ---
            var mediaPayload = payload;
            if (!string.IsNullOrWhiteSpace(payload.Audio))
            {
                var audioMarkup = "";
                if (audioUploadIdx is int aIdx && aIdx < storedNames.Count && !string.IsNullOrWhiteSpace(storedNames[aIdx]))
                {
                    var storedAudioName = storedNames[aIdx];
                    audioMarkup = $"[sound:{storedAudioName}]";
                }

                mediaPayload = WithAudio(payload, audioMarkup);
            }

            var dictionaryMediaTags = new Dictionary<string, string>();
            foreach (var (idx, originalFilename) in dictMediaIndices)
            {
                if (idx < storedNames.Count)
                {
                    var storedName = storedNames[idx];
                    if (!string.IsNullOrWhiteSpace(storedName))
                        dictionaryMediaTags[originalFilename] = AnkiMediaMarkup.ForDictionaryHtmlReference(storedName);
                }
            }

            // --- Phase 5: Render field templates ---
            var fieldMappings = AnkiFieldMappingResolver.ResolveForMining(
                noteType,
                _settings.FieldMappings,
                context);
            var renderedFields = new Dictionary<string, string>();
            foreach (var (fieldName, template) in fieldMappings)
            {
                if (string.IsNullOrWhiteSpace(template) || template == "-")
                    continue;

                var rendered = AnkiHandlebarRenderer.Render(template, mediaPayload, context);

                foreach (var (filename, tag) in dictionaryMediaTags)
                    rendered = rendered.Replace(filename, tag);

                if (!string.IsNullOrWhiteSpace(rendered))
                    renderedFields[fieldName] = rendered;
            }

            if (renderedFields.Count == 0)
            {
                Log.Warning("[Anki] No fields rendered");
                return null;
            }

            // --- Phase 6: Add note (+ optional sync) ---
            var addNoteSw = System.Diagnostics.Stopwatch.StartNew();
            var noteId = await client.AddNoteWithOptionalSyncAsync(
                deck, noteType, renderedFields, _settings, _settings.AnkiConnectForceSync);
            addNoteSw.Stop();
            Log.Information("[Anki] addNote completed in {ElapsedMs}ms success={Success} noteId={NoteId}",
                addNoteSw.ElapsedMilliseconds, noteId.HasValue, noteId);

            Log.Information("[Anki] Mine completed: expression={Expression}, success={Success}, noteId={NoteId}, total={TotalMs}ms, audioResolveDownload={AudioMs}ms, mediaRead={MediaReadMs}ms, mediaUpload={MediaUploadMs}ms, addNote={AddNoteMs}ms, batchCount={BatchCount}",
                payload.Expression, noteId.HasValue, noteId, totalSw.ElapsedMilliseconds, audioSw.ElapsedMilliseconds, mediaReadSw.ElapsedMilliseconds, mediaUploadSw.ElapsedMilliseconds, addNoteSw.ElapsedMilliseconds, uploads.Count);
            if (noteId.HasValue && !string.IsNullOrWhiteSpace(payload.Expression))
                _savedExpressions[payload.Expression] = 0;
            return noteId;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Anki] MineEntryAsync failed after {ElapsedMs}ms", totalSw.ElapsedMilliseconds);
            return null;
        }
    }

    public Task<bool> OpenNoteInAnkiAsync(long noteId) =>
        GetClient().OpenNoteInAnkiAsync(noteId);

    public async Task<bool> DuplicateCheckAsync(string rawPayloadJson)
    {
        try
        {
            var payload = AnkiMiningPayload.FromJson(rawPayloadJson);
            return await DuplicateCheckExpressionAsync(payload.Expression);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Anki] DuplicateCheckAsync failed");
            return false;
        }
    }

    public async Task<bool> DuplicateCheckExpressionAsync(string expression)
    {
        try
        {
            if (!_settings.IsConfigured || string.IsNullOrWhiteSpace(expression))
                return !string.IsNullOrWhiteSpace(expression) && _savedExpressions.ContainsKey(expression);

            var deck = ResolveDeck();
            var noteType = ResolveNoteType();
            var firstField = noteType?.Fields.FirstOrDefault();
            if (deck == null || noteType == null || string.IsNullOrWhiteSpace(firstField))
                return _savedExpressions.ContainsKey(expression);

            var fields = new Dictionary<string, string>
            {
                [firstField] = expression,
            };
            var canAdd = await GetClient().CanAddNotesAsync(deck, noteType, fields, _settings);
            if (!canAdd)
                _savedExpressions[expression] = 0;
            return !canAdd;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Anki] DuplicateCheckExpressionAsync failed");
            return _savedExpressions.ContainsKey(expression);
        }
    }

    public async Task<string?> GetWritableMediaDirectoryAsync()
    {
        if (IsWritableDirectory(_cachedWritableMediaDirectory))
            return _cachedWritableMediaDirectory;

        try
        {
            var mediaDirectory = await GetClient().GetMediaDirPathAsync();
            if (!IsWritableDirectory(mediaDirectory))
                return null;

            _cachedWritableMediaDirectory = mediaDirectory;
            return mediaDirectory;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Anki] Could not resolve writable collection.media directory");
            return null;
        }
    }

    private Dictionary<string, string> RenderFieldsForDuplicateCheck(
        AnkiNoteType noteType,
        AnkiMiningPayload payload,
        AnkiMiningContext context)
    {
        var renderedFields = new Dictionary<string, string>();
        var fieldMappings = AnkiFieldMappingResolver.ResolveForMining(
            noteType,
            _settings.FieldMappings,
            context);
        foreach (var (fieldName, template) in fieldMappings)
        {
            if (string.IsNullOrWhiteSpace(template) || template == "-")
                continue;

            var rendered = AnkiHandlebarRenderer.Render(template, payload, context);
            if (!string.IsNullOrWhiteSpace(rendered))
                renderedFields[fieldName] = rendered;
        }

        return renderedFields;
    }

    private static bool IsWritableDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        var probe = Path.Combine(directory, $".niratan-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(probe);
            }
            catch
            {
            }

            return false;
        }
    }

    private AnkiDeck? ResolveDeck()
    {
        var decks = _settings.AvailableDecks;
        if (decks.Count == 0) return null;

        return decks.FirstOrDefault(d => d.Id == _settings.SelectedDeckId)
               ?? decks.FirstOrDefault(d => d.Name == _settings.SelectedDeckName);
    }

    private AnkiNoteType? ResolveNoteType()
    {
        var noteTypes = _settings.AvailableNoteTypes;
        if (noteTypes.Count == 0) return null;

        return noteTypes.FirstOrDefault(nt => nt.Id == _settings.SelectedNoteTypeId)
               ?? noteTypes.FirstOrDefault(nt => nt.Name == _settings.SelectedNoteTypeName);
    }

    private static readonly HttpClient s_audioHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly AnkiAudioDownloader s_audioDownloader = new(s_audioHttpClient);

    internal async Task<byte[]?> ResolveDictionaryMediaAsync(DictionaryMedia media)
    {
        if (string.IsNullOrWhiteSpace(media.Path))
            return null;

        if (!string.IsNullOrWhiteSpace(media.Dictionary))
        {
            var dictionaryBytes = await _dictionaryLookupService.GetMediaFileAsync(media.Dictionary, media.Path);
            if (dictionaryBytes is { Length: > 0 })
                return dictionaryBytes;
        }

        if (!File.Exists(media.Path))
            return null;

        return await File.ReadAllBytesAsync(media.Path);
    }

    private static AnkiMiningPayload WithAudio(AnkiMiningPayload payload, string audio) =>
        new()
        {
            Expression = payload.Expression,
            Reading = payload.Reading,
            Matched = payload.Matched,
            FuriganaPlain = payload.FuriganaPlain,
            FrequenciesHtml = payload.FrequenciesHtml,
            FreqHarmonicRank = payload.FreqHarmonicRank,
            Glossary = payload.Glossary,
            GlossaryFirst = payload.GlossaryFirst,
            SingleGlossariesJson = payload.SingleGlossariesJson,
            PitchPositions = payload.PitchPositions,
            PitchCategories = payload.PitchCategories,
            PopupSelectionText = payload.PopupSelectionText,
            Audio = audio,
            SelectedDictionary = payload.SelectedDictionary,
            DictionaryMediaJson = payload.DictionaryMediaJson,
        };

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
