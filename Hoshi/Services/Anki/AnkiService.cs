using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hoshi.Models.Anki;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;
using Serilog;

namespace Hoshi.Services.Anki;

public sealed class AnkiService : IAnkiService, IDisposable
{
    private readonly ISettingsService _settingsService;
    private AnkiConnectClient? _client;
    private AnkiSettings _settings;

    public AnkiSettings Settings => _settings;

    public AnkiService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        _settings = settingsService.Current.AnkiSettings;
    }

    public void UpdateSettings(AnkiSettings settings)
    {
        _settings = settings;
        _client?.Dispose();
        _client = null;
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

    public async Task<bool> MineEntryAsync(string rawPayloadJson, AnkiMiningContext context)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (!_settings.IsConfigured)
            {
                Log.Warning("[Anki] Not configured");
                return false;
            }

            var payload = AnkiMiningPayload.FromJson(rawPayloadJson);

            // Resolve deck
            var deck = ResolveDeck();
            if (deck == null)
            {
                Log.Warning("[Anki] Deck not found (id={DeckId}, name={DeckName})",
                    _settings.SelectedDeckId, _settings.SelectedDeckName);
                return false;
            }

            // Resolve note type
            var noteType = ResolveNoteType();
            if (noteType == null)
            {
                Log.Warning("[Anki] Note type not found (id={NoteTypeId}, name={NoteTypeName})",
                    _settings.SelectedNoteTypeId, _settings.SelectedNoteTypeName);
                return false;
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
            int? videoScreenshotUploadIdx = null;
            int? videoAudioClipUploadIdx = null;
            var dictMediaIndices = new List<(int idx, string originalFilename)>();

            if (!string.IsNullOrWhiteSpace(context.CoverPath) && File.Exists(context.CoverPath))
            {
                try
                {
                    var bytes = await File.ReadAllBytesAsync(context.CoverPath);
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

            if (videoScreenshotUploadIdx is int screenshotIdx && screenshotIdx < storedNames.Count)
                context.VideoScreenshotTag = GetMediaTag(storedNames[screenshotIdx]);

            if (videoAudioClipUploadIdx is int videoAudioIdx && videoAudioIdx < storedNames.Count)
                context.VideoAudioClipTag = GetMediaTag(storedNames[videoAudioIdx]);

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
                    var tag = GetMediaTag(storedNames[idx]);
                    dictionaryMediaTags[originalFilename] = tag;
                }
            }

            // --- Phase 5: Render field templates ---
            var renderedFields = new Dictionary<string, string>();
            foreach (var (fieldName, template) in _settings.FieldMappings)
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
                return false;
            }

            // --- Phase 6: Add note (+ optional sync) ---
            var addNoteSw = System.Diagnostics.Stopwatch.StartNew();
            var success = await client.AddNoteWithOptionalSyncAsync(
                deck, noteType, renderedFields, _settings, _settings.AnkiConnectForceSync);
            addNoteSw.Stop();
            Log.Information("[Anki] addNote completed in {ElapsedMs}ms success={Success}",
                addNoteSw.ElapsedMilliseconds, success);

            Log.Information("[Anki] Mine completed: expression={Expression}, success={Success}, total={TotalMs}ms, audioResolveDownload={AudioMs}ms, mediaRead={MediaReadMs}ms, mediaUpload={MediaUploadMs}ms, addNote={AddNoteMs}ms, batchCount={BatchCount}",
                payload.Expression, success, totalSw.ElapsedMilliseconds, audioSw.ElapsedMilliseconds, mediaReadSw.ElapsedMilliseconds, mediaUploadSw.ElapsedMilliseconds, addNoteSw.ElapsedMilliseconds, uploads.Count);
            return success;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Anki] MineEntryAsync failed after {ElapsedMs}ms", totalSw.ElapsedMilliseconds);
            return false;
        }
    }

    public async Task<bool> DuplicateCheckAsync(string rawPayloadJson)
    {
        try
        {
            if (!_settings.IsConfigured)
                return false;

            var payload = AnkiMiningPayload.FromJson(rawPayloadJson);
            var deck = ResolveDeck();
            var noteType = ResolveNoteType();
            if (deck == null || noteType == null)
                return false;

            var renderedFields = new Dictionary<string, string>();
            foreach (var (fieldName, template) in _settings.FieldMappings)
            {
                if (string.IsNullOrWhiteSpace(template) || template == "-")
                    continue;

                var rendered = AnkiHandlebarRenderer.Render(template, payload, new AnkiMiningContext());
                if (!string.IsNullOrWhiteSpace(rendered))
                    renderedFields[fieldName] = rendered;
            }

            var canAdd = await GetClient().CanAddNotesAsync(deck, noteType, renderedFields, _settings);
            return !canAdd; // Return true if duplicate exists
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Anki] DuplicateCheckAsync failed");
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

    private static async Task<byte[]?> ResolveDictionaryMediaAsync(DictionaryMedia media)
    {
        if (string.IsNullOrWhiteSpace(media.Path) || !File.Exists(media.Path))
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

    private static string GetMediaTag(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();

        if (ext is ".mp3" or ".aac" or ".m4a" or ".wav" or ".ogg")
            return $"[sound:{filename}]";

        if (ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".avif" or ".svg")
            return $"<img src=\"{filename}\">";

        return filename;
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
