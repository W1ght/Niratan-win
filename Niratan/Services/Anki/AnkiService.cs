using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Anki;
using Niratan.Models.DTO;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.Settings;
using Serilog;

namespace Niratan.Services.Anki;

public sealed class AnkiService : IAnkiService, IDisposable
{
    private sealed record CachedDuplicateLookup(
        AnkiDuplicateLookupResult Result,
        DateTimeOffset ExpiresAt,
        long SettingsGeneration);

    private sealed record SavedDuplicateLookupEntry(
        IReadOnlyList<long> NoteIds,
        long SettingsGeneration);

    private sealed record PreparedMedia(string Filename, byte[] Data);

    private sealed record PreparedDictionaryMedia(
        string OriginalFilename,
        string Filename,
        byte[] Data);

    private readonly record struct MiningSubmissionKey(
        long SettingsGeneration,
        string Expression);

    private sealed class MiningSubmissionGateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private static readonly TimeSpan DuplicateCacheDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NotDuplicateCacheDuration = TimeSpan.FromSeconds(12);
    private const int MaxDuplicateLookupCacheEntries = 512;

    private readonly ISettingsService _settingsService;
    private readonly IDictionaryLookupService _dictionaryLookupService;
    private readonly Func<string, AnkiConnectClient> _clientFactory;
    private readonly object _clientLock = new();
    private readonly object _miningSubmissionGatesLock = new();
    private readonly SemaphoreSlim _duplicateLookupGate = new(1, 1);
    private readonly Dictionary<MiningSubmissionKey, MiningSubmissionGateEntry> _miningSubmissionGates = [];
    private AnkiConnectClient? _client;
    private AnkiSettings _settings;
    private Task<string?>? _writableMediaDirectoryTask;
    private long _settingsGeneration;
    private int _disposed;
    private readonly ConcurrentDictionary<string, SavedDuplicateLookupEntry> _savedDuplicateLookups =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CachedDuplicateLookup> _duplicateLookupCache =
        new(StringComparer.Ordinal);

    public AnkiSettings Settings => _settings;

    public AnkiService(ISettingsService settingsService, IDictionaryLookupService dictionaryLookupService)
        : this(settingsService, dictionaryLookupService, static url => new AnkiConnectClient(url))
    {
    }

    internal AnkiService(
        ISettingsService settingsService,
        IDictionaryLookupService dictionaryLookupService,
        Func<string, AnkiConnectClient> clientFactory)
    {
        _settingsService = settingsService;
        _dictionaryLookupService = dictionaryLookupService;
        _clientFactory = clientFactory;
        _settings = settingsService.Current.AnkiSettings;
        _settingsService.SettingChanged += SettingsService_SettingChanged;
    }

    public void UpdateSettings(AnkiSettings settings)
    {
        lock (_clientLock)
        {
            _settings = settings;
            Interlocked.Increment(ref _settingsGeneration);
            _client?.Dispose();
            _client = null;
            _writableMediaDirectoryTask = null;
        }
        _savedDuplicateLookups.Clear();
        _duplicateLookupCache.Clear();
    }

    private void SettingsService_SettingChanged(object? sender, SettingsChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppSettings.AnkiSettings) or nameof(ISettingsService.Current))
            UpdateSettings(_settingsService.Current.AnkiSettings);
    }

    private AnkiConnectClient GetClient()
    {
        lock (_clientLock)
        {
            if (_client == null)
            {
                var url = _settings.AnkiConnectUrl;
                if (string.IsNullOrWhiteSpace(url))
                    url = "http://localhost:8765";
                _client = _clientFactory(url);
            }
            return _client;
        }
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
                var duplicateLookup = await DuplicateLookupExpressionAsync(payload.Expression);
                if (duplicateLookup.IsDuplicate)
                    return AnkiMiningPreflightResult.Duplicate(duplicateLookup.NoteIds);
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
            long settingsGeneration;
            AnkiSettings settings;
            AnkiConnectClient? client;
            lock (_clientLock)
            {
                settingsGeneration = Volatile.Read(ref _settingsGeneration);
                settings = AnkiSettings.Clone(_settings);
                client = settings.IsConfigured ? GetClient() : null;
            }

            if (!settings.IsConfigured || client == null)
            {
                Log.Warning("[Anki] Not configured");
                return null;
            }

            var payload = AnkiMiningPayload.FromJson(rawPayloadJson);

            // Resolve deck
            var deck = ResolveDeck(settings);
            if (deck == null)
            {
                Log.Warning("[Anki] Deck not found (id={DeckId}, name={DeckName})",
                    settings.SelectedDeckId, settings.SelectedDeckName);
                return null;
            }

            // Resolve note type
            var noteType = ResolveNoteType(settings);
            if (noteType == null)
            {
                Log.Warning("[Anki] Note type not found (id={NoteTypeId}, name={NoteTypeName})",
                    settings.SelectedNoteTypeId, settings.SelectedNoteTypeName);
                return null;
            }

            var requiredMediaNeeds = AnkiFieldMappingResolver.ResolveMediaNeedsForMining(
                noteType,
                settings.FieldMappings,
                context);
            var requiresBookCover = AnkiFieldMappingResolver.ResolveForMining(
                    noteType,
                    settings.FieldMappings,
                    context)
                .Values
                .Any(template => template.Contains("{book-cover}", StringComparison.Ordinal));
            var isVideoMiningContext = !string.IsNullOrWhiteSpace(context.VideoFileName)
                || !string.IsNullOrWhiteSpace(context.VideoTimestamp)
                || !string.IsNullOrWhiteSpace(context.VideoSubtitle)
                || !string.IsNullOrWhiteSpace(context.VideoScreenshotPath)
                || !string.IsNullOrWhiteSpace(context.VideoScreenshotTag)
                || !string.IsNullOrWhiteSpace(context.VideoAudioClipPath)
                || !string.IsNullOrWhiteSpace(context.VideoAudioClipTag);

            // Start independent media work together. Remote word audio and the
            // collection.media lookup no longer hold up local/dictionary reads.
            var audioSw = System.Diagnostics.Stopwatch.StartNew();
            var remoteAudioTask = DownloadRemoteAudioAsync(payload.Audio);
            var shouldResolveDirectMediaDirectory = HasPotentialMedia(payload, context, settings);
            var directMediaDirectoryTask = shouldResolveDirectMediaDirectory
                ? GetWritableMediaDirectoryAsync()
                : Task.FromResult<string?>(null);

            // --- Phase 1: Resolve local and dictionary media concurrently ---
            var mediaReadSw = System.Diagnostics.Stopwatch.StartNew();
            var uploads = new List<(string filename, byte[] data)>();
            // Track which upload indices correspond to what
            int? audioUploadIdx = null;
            int? coverUploadIdx = null;
            int? sasayakiAudioUploadIdx = null;
            int? videoScreenshotUploadIdx = null;
            int? videoAudioClipUploadIdx = null;
            var dictMediaIndices = new List<(int idx, string originalFilename)>();

            var picturePath = !string.IsNullOrWhiteSpace(context.MangaPagePath)
                ? context.MangaPagePath
                : context.CoverPath;
            var hadSasayakiAudioPath = !string.IsNullOrWhiteSpace(context.SasayakiAudioPath);
            var isMangaPicture = !string.IsNullOrWhiteSpace(context.MangaPagePath);
            var pictureTask = ReadLocalMediaAsync(
                picturePath,
                (path, bytes) => isMangaPicture
                    ? CreateMangaPageMediaFilename(path, bytes)
                    : CreateCoverMediaFilename(path, bytes),
                "picture image");
            var sasayakiAudioTask = ReadLocalMediaAsync(
                context.SasayakiAudioPath,
                static (path, _) => Path.GetFileName(path),
                "sasayaki audio");
            var videoScreenshotTask = ReadLocalMediaAsync(
                context.VideoScreenshotPath,
                static (path, _) => Path.GetFileName(path),
                "video screenshot");
            var videoAudioClipTask = ReadLocalMediaAsync(
                context.VideoAudioClipPath,
                static (path, _) => Path.GetFileName(path),
                "video audio clip");
            var dictionaryMediaTask = settings.EmbedMedia
                ? ResolveDictionaryMediaListAsync(payload.DictionaryMediaList)
                : Task.FromResult<IReadOnlyList<PreparedDictionaryMedia>>([]);

            await Task.WhenAll(
                pictureTask,
                sasayakiAudioTask,
                videoScreenshotTask,
                videoAudioClipTask,
                dictionaryMediaTask);

            AddPreparedMedia(await pictureTask, ref coverUploadIdx, uploads);
            AddPreparedMedia(await sasayakiAudioTask, ref sasayakiAudioUploadIdx, uploads);
            AddPreparedMedia(await videoScreenshotTask, ref videoScreenshotUploadIdx, uploads);
            AddPreparedMedia(await videoAudioClipTask, ref videoAudioClipUploadIdx, uploads);
            foreach (var media in await dictionaryMediaTask)
            {
                dictMediaIndices.Add((uploads.Count, media.OriginalFilename));
                uploads.Add((media.Filename, media.Data));
            }
            mediaReadSw.Stop();
            Log.Information("[Anki] mediaRead completed in {ElapsedMs}ms uploadCount={UploadCount}",
                mediaReadSw.ElapsedMilliseconds, uploads.Count);

            // Direct writes for already-prepared local media can overlap the
            // remaining word-audio download.
            var mediaUploadSw = System.Diagnostics.Stopwatch.StartNew();
            var directMediaDirectory = await directMediaDirectoryTask;
            var directWriteTasks = StartDirectMediaWrites(uploads, directMediaDirectory);
            var remoteAudio = await remoteAudioTask;
            audioSw.Stop();
            Log.Information("[Anki] audioResolve/download completed in {ElapsedMs}ms hasAudio={HasAudio}",
                audioSw.ElapsedMilliseconds, remoteAudio != null);
            if (remoteAudio != null)
            {
                audioUploadIdx = uploads.Count;
                uploads.Add((remoteAudio.Filename, remoteAudio.Bytes));
                if (!string.IsNullOrWhiteSpace(directMediaDirectory))
                {
                    directWriteTasks.Add(AnkiDirectMediaStore.WriteBytesAsync(
                        directMediaDirectory,
                        remoteAudio.Filename,
                        remoteAudio.Bytes));
                }
            }

            // --- Phase 2: Direct-write concurrently; batch-upload only fallbacks ---
            var storedNames = await StoreMediaFilesAsync(
                client,
                uploads,
                directMediaDirectory,
                directWriteTasks);
            mediaUploadSw.Stop();
            Log.Information(
                "[Anki] mediaStore completed in {ElapsedMs}ms mediaCount={MediaCount} direct={Direct}",
                mediaUploadSw.ElapsedMilliseconds,
                uploads.Count,
                !string.IsNullOrWhiteSpace(directMediaDirectory));

            if (coverUploadIdx is int coverIdx
                && coverIdx < storedNames.Count
                && !string.IsNullOrWhiteSpace(storedNames[coverIdx]))
            {
                context.CoverTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[coverIdx]);
            }

            if (videoScreenshotUploadIdx is int screenshotIdx
                && screenshotIdx < storedNames.Count
                && !string.IsNullOrWhiteSpace(storedNames[screenshotIdx]))
                context.VideoScreenshotTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[screenshotIdx]);

            if (videoAudioClipUploadIdx is int videoAudioIdx
                && videoAudioIdx < storedNames.Count
                && !string.IsNullOrWhiteSpace(storedNames[videoAudioIdx]))
                context.VideoAudioClipTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[videoAudioIdx]);

            if (sasayakiAudioUploadIdx is int sasayakiAudioIdx
                && sasayakiAudioIdx < storedNames.Count
                && !string.IsNullOrWhiteSpace(storedNames[sasayakiAudioIdx]))
                context.SasayakiAudioTag = AnkiMediaMarkup.ForFieldPlaceholder(storedNames[sasayakiAudioIdx]);

            // A local path must never escape into an Anki field. Generated media is
            // represented only by a ready collection.media filename/tag.
            context.SasayakiAudioPath = null;
            context.VideoScreenshotPath = null;
            context.VideoAudioClipPath = null;

            if ((isVideoMiningContext
                 && requiredMediaNeeds.NeedsVideoScreenshot
                 && string.IsNullOrWhiteSpace(context.VideoScreenshotTag))
                || (isVideoMiningContext
                    && requiredMediaNeeds.NeedsVideoAudioClip
                    && string.IsNullOrWhiteSpace(context.VideoAudioClipTag))
                || (!isVideoMiningContext
                    && requiresBookCover
                    && !string.IsNullOrWhiteSpace(picturePath)
                    && string.IsNullOrWhiteSpace(context.CoverTag))
                || (requiredMediaNeeds.NeedsSasayakiAudio
                    && hadSasayakiAudioPath
                    && string.IsNullOrWhiteSpace(context.SasayakiAudioTag)))
            {
                Log.Warning("[Anki] Required mining media was not ready; note submission skipped");
                return null;
            }

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
                settings.FieldMappings,
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
            if (settings.AllowDupes && !string.IsNullOrWhiteSpace(payload.Expression))
                _duplicateLookupCache.TryRemove(payload.Expression, out _);
            var addNoteSw = System.Diagnostics.Stopwatch.StartNew();
            long? noteId;
            if (!settings.AllowDupes && !string.IsNullOrWhiteSpace(payload.Expression))
            {
                var submissionKey = new MiningSubmissionKey(
                    settingsGeneration,
                    payload.Expression);
                var submissionGate = await AcquireMiningSubmissionGateAsync(submissionKey);
                try
                {
                    if (settingsGeneration != Volatile.Read(ref _settingsGeneration))
                        return null;

                    var finalDuplicateLookup = await ForceDuplicateLookupForSubmissionAsync(
                        payload.Expression,
                        settingsGeneration,
                        settings,
                        deck,
                        noteType,
                        client);
                    if (settingsGeneration != Volatile.Read(ref _settingsGeneration)
                        || finalDuplicateLookup.IsDuplicate)
                    {
                        return null;
                    }

                    noteId = await client.AddNoteWithOptionalSyncAsync(
                        deck,
                        noteType,
                        renderedFields,
                        settings,
                        settings.AnkiConnectForceSync);
                    if (!noteId.HasValue)
                        _duplicateLookupCache.TryRemove(payload.Expression, out _);
                    CacheSuccessfulMiningResult(payload.Expression, noteId, settingsGeneration);
                }
                finally
                {
                    ReleaseMiningSubmissionGate(submissionKey, submissionGate);
                }
            }
            else
            {
                if (settingsGeneration != Volatile.Read(ref _settingsGeneration))
                    return null;

                noteId = await client.AddNoteWithOptionalSyncAsync(
                    deck,
                    noteType,
                    renderedFields,
                    settings,
                    settings.AnkiConnectForceSync);
                CacheSuccessfulMiningResult(payload.Expression, noteId, settingsGeneration);
            }
            addNoteSw.Stop();
            Log.Information("[Anki] addNote completed in {ElapsedMs}ms success={Success} noteId={NoteId}",
                addNoteSw.ElapsedMilliseconds, noteId.HasValue, noteId);

            Log.Information("[Anki] Mine completed: expression={Expression}, success={Success}, noteId={NoteId}, total={TotalMs}ms, audioResolveDownload={AudioMs}ms, mediaRead={MediaReadMs}ms, mediaUpload={MediaUploadMs}ms, addNote={AddNoteMs}ms, batchCount={BatchCount}",
                payload.Expression, noteId.HasValue, noteId, totalSw.ElapsedMilliseconds, audioSw.ElapsedMilliseconds, mediaReadSw.ElapsedMilliseconds, mediaUploadSw.ElapsedMilliseconds, addNoteSw.ElapsedMilliseconds, uploads.Count);
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

    private static async Task<AnkiAudioDownloadResult?> DownloadRemoteAudioAsync(string audioSource)
    {
        if (string.IsNullOrWhiteSpace(audioSource))
            return null;

        try
        {
            return await s_audioDownloader.DownloadAsync(audioSource);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Anki] Failed to resolve/download audio");
            return null;
        }
    }

    private static async Task<PreparedMedia?> ReadLocalMediaAsync(
        string? path,
        Func<string, byte[], string> filenameFactory,
        string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            if (bytes.Length == 0)
                return null;

            return new PreparedMedia(filenameFactory(path, bytes), bytes);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Anki] Failed to read {Description}", description);
            return null;
        }
    }

    private async Task<IReadOnlyList<PreparedDictionaryMedia>> ResolveDictionaryMediaListAsync(
        IReadOnlyList<DictionaryMedia> dictionaryMedia)
    {
        var prepared = new List<PreparedDictionaryMedia>(dictionaryMedia.Count);
        foreach (var media in dictionaryMedia)
        {
            try
            {
                var bytes = await ResolveDictionaryMediaAsync(media);
                if (bytes is not { Length: > 0 })
                    continue;

                var originalFilename = string.IsNullOrWhiteSpace(media.Filename)
                    ? Path.GetFileName(media.Path)
                    : media.Filename;
                if (string.IsNullOrWhiteSpace(originalFilename))
                    continue;

                prepared.Add(new PreparedDictionaryMedia(
                    originalFilename,
                    CreateDictionaryMediaFilename(
                        originalFilename,
                        bytes),
                    bytes));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Anki] Failed to read dictionary media {Filename}", media.Filename);
            }
        }

        return prepared;
    }

    private static void AddPreparedMedia(
        PreparedMedia? media,
        ref int? uploadIndex,
        List<(string filename, byte[] data)> uploads)
    {
        if (media == null)
            return;

        uploadIndex = uploads.Count;
        uploads.Add((media.Filename, media.Data));
    }

    private static bool HasPotentialMedia(
        AnkiMiningPayload payload,
        AnkiMiningContext context,
        AnkiSettings settings) =>
        !string.IsNullOrWhiteSpace(payload.Audio)
        || !string.IsNullOrWhiteSpace(context.MangaPagePath)
        || !string.IsNullOrWhiteSpace(context.CoverPath)
        || !string.IsNullOrWhiteSpace(context.SasayakiAudioPath)
        || !string.IsNullOrWhiteSpace(context.VideoScreenshotPath)
        || !string.IsNullOrWhiteSpace(context.VideoAudioClipPath)
        || (settings.EmbedMedia && payload.DictionaryMediaList.Count > 0);

    private static List<Task<string?>> StartDirectMediaWrites(
        IReadOnlyList<(string filename, byte[] data)> media,
        string? directMediaDirectory)
    {
        if (string.IsNullOrWhiteSpace(directMediaDirectory))
            return [];

        return media
            .Select(item => AnkiDirectMediaStore.WriteBytesAsync(
                directMediaDirectory,
                item.filename,
                item.data))
            .ToList();
    }

    private static async Task<List<string>> StoreMediaFilesAsync(
        AnkiConnectClient client,
        List<(string filename, byte[] data)> media,
        string? directMediaDirectory,
        IReadOnlyList<Task<string?>> directWrites)
    {
        if (media.Count == 0)
            return [];

        var storedNames = Enumerable.Repeat("", media.Count).ToList();
        var fallbackIndices = new List<int>();
        if (!string.IsNullOrWhiteSpace(directMediaDirectory))
        {
            var directNames = await Task.WhenAll(directWrites);
            for (var index = 0; index < media.Count; index++)
            {
                if (index < directNames.Length
                    && !string.IsNullOrWhiteSpace(directNames[index]))
                    storedNames[index] = directNames[index]!;
                else
                    fallbackIndices.Add(index);
            }
        }
        else
        {
            fallbackIndices.AddRange(Enumerable.Range(0, media.Count));
        }

        if (fallbackIndices.Count == 0)
            return storedNames;

        try
        {
            var fallbackFiles = fallbackIndices
                .Select(index => media[index])
                .ToList();
            var fallbackNames = await client.StoreMediaFilesAsync(fallbackFiles);
            for (var index = 0; index < fallbackIndices.Count && index < fallbackNames.Count; index++)
                storedNames[fallbackIndices[index]] = fallbackNames[index];
        }
        catch (Exception ex)
        {
            Log.Warning(
                ex,
                "[Anki] Batch media upload fallback failed ({Count} files)",
                fallbackIndices.Count);
        }

        return storedNames;
    }

    internal static string CreateMangaPageMediaFilename(
        string path,
        byte[] bytes)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif"
            or ".webp" or ".avif"))
        {
            extension = ".png";
        }
        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        return $"niratan_manga_page_{hash}{extension}";
    }

    internal static string CreateCoverMediaFilename(string path, byte[] bytes) =>
        CreateContentAddressedMediaFilename("niratan_cover", path, bytes, ".png");

    internal static string CreateDictionaryMediaFilename(string path, byte[] bytes) =>
        CreateContentAddressedMediaFilename("niratan_dict", path, bytes, ".bin");

    private static string CreateContentAddressedMediaFilename(
        string prefix,
        string sourceName,
        byte[] bytes,
        string fallbackExtension)
    {
        var extension = Path.GetExtension(sourceName).ToLowerInvariant();
        if (extension.Length is < 2 or > 12
            || extension.Skip(1).Any(ch => !char.IsAsciiLetterOrDigit(ch)))
        {
            extension = fallbackExtension;
        }

        var hash = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        return $"{prefix}_{hash}{extension}";
    }

    private async Task<MiningSubmissionGateEntry> AcquireMiningSubmissionGateAsync(
        MiningSubmissionKey key)
    {
        MiningSubmissionGateEntry gate;
        lock (_miningSubmissionGatesLock)
        {
            if (!_miningSubmissionGates.TryGetValue(key, out gate!))
            {
                gate = new MiningSubmissionGateEntry();
                _miningSubmissionGates[key] = gate;
            }

            gate.ReferenceCount++;
        }

        try
        {
            await gate.Semaphore.WaitAsync();
            return gate;
        }
        catch
        {
            ReleaseMiningSubmissionGateReference(key, gate);
            throw;
        }
    }

    private void ReleaseMiningSubmissionGate(
        MiningSubmissionKey key,
        MiningSubmissionGateEntry gate)
    {
        gate.Semaphore.Release();
        ReleaseMiningSubmissionGateReference(key, gate);
    }

    private void ReleaseMiningSubmissionGateReference(
        MiningSubmissionKey key,
        MiningSubmissionGateEntry gate)
    {
        var dispose = false;
        lock (_miningSubmissionGatesLock)
        {
            gate.ReferenceCount--;
            if (gate.ReferenceCount == 0
                && _miningSubmissionGates.TryGetValue(key, out var current)
                && ReferenceEquals(current, gate))
            {
                _miningSubmissionGates.Remove(key);
                dispose = true;
            }
        }

        if (dispose)
            gate.Semaphore.Dispose();
    }

    private async Task<AnkiDuplicateLookupResult> ForceDuplicateLookupForSubmissionAsync(
        string expression,
        long settingsGeneration,
        AnkiSettings settings,
        AnkiDeck deck,
        AnkiNoteType noteType,
        AnkiConnectClient client)
    {
        if (TryGetCachedDuplicateLookup(expression, settingsGeneration, out var cached)
            && cached.IsDuplicate)
        {
            return cached;
        }

        var firstField = noteType.Fields.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstField))
            return AnkiDuplicateLookupResult.NotDuplicate();

        var canAdd = await client.CanAddNotesAsync(
            deck,
            noteType,
            [new Dictionary<string, string> { [firstField] = expression }],
            settings);
        if (canAdd.Count > 0 && canAdd[0])
        {
            var notDuplicate = AnkiDuplicateLookupResult.NotDuplicate();
            CacheDuplicateLookup(expression, notDuplicate, settingsGeneration);
            return notDuplicate;
        }

        var query = BuildDuplicateSearchQuery(expression, deck, noteType, settings);
        var noteIdsByQuery = await client.FindNotesAsync([query]);
        var noteIds = noteIdsByQuery.Count > 0
            ? noteIdsByQuery[0]
                .Where(noteId => noteId > 0)
                .Distinct()
                .ToArray()
            : [];
        var duplicate = AnkiDuplicateLookupResult.Duplicate(noteIds);
        SaveDuplicateLookup(expression, noteIds, settingsGeneration);
        CacheDuplicateLookup(expression, duplicate, settingsGeneration);
        return duplicate;
    }

    private void CacheSuccessfulMiningResult(
        string expression,
        long? noteId,
        long settingsGeneration)
    {
        if (noteId is not long addedNoteId || string.IsNullOrWhiteSpace(expression))
            return;

        SaveDuplicateLookup(expression, [addedNoteId], settingsGeneration);
        CacheDuplicateLookup(
            expression,
            AnkiDuplicateLookupResult.Duplicate([addedNoteId]),
            settingsGeneration);
    }

    public Task<bool> OpenNotesInAnkiAsync(IReadOnlyList<long> noteIds) =>
        GetClient().OpenNotesInAnkiAsync(noteIds);

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

    public async Task<AnkiDuplicateLookupResult> DuplicateLookupExpressionAsync(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return AnkiDuplicateLookupResult.NotDuplicate();

        var results = await DuplicateLookupExpressionsAsync([expression]);
        return results.TryGetValue(expression, out var result)
            ? result
            : AnkiDuplicateLookupResult.NotDuplicate();
    }

    public async Task<bool> DuplicateCheckExpressionAsync(string expression) =>
        (await DuplicateLookupExpressionAsync(expression)).IsDuplicate;

    public async Task<IReadOnlyDictionary<string, AnkiDuplicateLookupResult>> DuplicateLookupExpressionsAsync(
        IReadOnlyList<string> expressions)
    {
        var uniqueExpressions = expressions
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var fastGeneration = Volatile.Read(ref _settingsGeneration);
        var results = BuildCachedDuplicateLookupResults(
            uniqueExpressions,
            fastGeneration,
            out var missing);
        if (missing.Length == 0
            && fastGeneration == Volatile.Read(ref _settingsGeneration))
        {
            return results;
        }

        var settingsGeneration = fastGeneration;
        await _duplicateLookupGate.WaitAsync();
        try
        {
            AnkiSettings settings;
            AnkiConnectClient? client;
            lock (_clientLock)
            {
                settingsGeneration = Volatile.Read(ref _settingsGeneration);
                settings = AnkiSettings.Clone(_settings);
                client = settings.IsConfigured ? GetClient() : null;
            }

            // Rebuild every result after acquiring the lane. A profile switch while
            // waiting invalidates both the misses and the cache hits collected above.
            results = BuildCachedDuplicateLookupResults(
                uniqueExpressions,
                settingsGeneration,
                out missing);
            if (settingsGeneration != Volatile.Read(ref _settingsGeneration))
                return BuildCurrentSavedDuplicateLookupResults(uniqueExpressions);
            if (missing.Length == 0)
                return results;

            if (!settings.IsConfigured)
            {
                foreach (var expression in missing)
                    results[expression] = SavedDuplicateLookup(expression, settingsGeneration);
                return settingsGeneration == Volatile.Read(ref _settingsGeneration)
                    ? results
                    : BuildCurrentSavedDuplicateLookupResults(uniqueExpressions);
            }

            var deck = ResolveDeck(settings);
            var noteType = ResolveNoteType(settings);
            var firstField = noteType?.Fields.FirstOrDefault();
            if (deck == null || noteType == null || string.IsNullOrWhiteSpace(firstField))
            {
                foreach (var expression in missing)
                    results[expression] = SavedDuplicateLookup(expression, settingsGeneration);
                return settingsGeneration == Volatile.Read(ref _settingsGeneration)
                    ? results
                    : BuildCurrentSavedDuplicateLookupResults(uniqueExpressions);
            }

            var fields = missing
                .Select(expression => new Dictionary<string, string>
                {
                    [firstField] = expression,
                })
                .ToArray();
            var canAdd = await client!.CanAddNotesAsync(deck, noteType, fields, settings);
            var duplicateExpressions = missing
                .Where((_, index) => index >= canAdd.Count || !canAdd[index])
                .ToArray();
            var duplicateQueries = duplicateExpressions
                .Select(expression => BuildDuplicateSearchQuery(expression, deck, noteType, settings))
                .ToArray();
            var duplicateNoteIds = await client.FindNotesAsync(duplicateQueries);

            // A profile/settings switch can dispose the old client while its request is
            // already in flight. Never let a late result populate or represent the new
            // profile's cache.
            if (settingsGeneration != Volatile.Read(ref _settingsGeneration))
                return BuildCurrentSavedDuplicateLookupResults(uniqueExpressions);

            var duplicateIndex = 0;
            for (var index = 0; index < missing.Length; index++)
            {
                AnkiDuplicateLookupResult result;
                if (index < canAdd.Count && canAdd[index])
                {
                    result = AnkiDuplicateLookupResult.NotDuplicate();
                }
                else
                {
                    var noteIds = duplicateIndex < duplicateNoteIds.Count
                        ? duplicateNoteIds[duplicateIndex]
                            .Where(noteId => noteId > 0)
                            .Distinct()
                            .ToArray()
                        : [];
                    duplicateIndex++;
                    SaveDuplicateLookup(missing[index], noteIds, settingsGeneration);
                    result = AnkiDuplicateLookupResult.Duplicate(noteIds);
                }

                results[missing[index]] = result;
                CacheDuplicateLookup(missing[index], result, settingsGeneration);
            }

            return settingsGeneration == Volatile.Read(ref _settingsGeneration)
                ? results
                : BuildCurrentSavedDuplicateLookupResults(uniqueExpressions);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Anki] DuplicateLookupExpressionsAsync failed");
            var fallbackGeneration = Volatile.Read(ref _settingsGeneration);
            if (fallbackGeneration != settingsGeneration)
                return BuildCurrentSavedDuplicateLookupResults(uniqueExpressions);

            foreach (var expression in missing)
                results[expression] = SavedDuplicateLookup(expression, fallbackGeneration);
            return results;
        }
        finally
        {
            _duplicateLookupGate.Release();
        }
    }

    private Dictionary<string, AnkiDuplicateLookupResult> BuildCachedDuplicateLookupResults(
        IReadOnlyList<string> expressions,
        long settingsGeneration,
        out string[] missing)
    {
        var results = new Dictionary<string, AnkiDuplicateLookupResult>(StringComparer.Ordinal);
        foreach (var expression in expressions)
        {
            if (string.IsNullOrWhiteSpace(expression))
                results[expression] = AnkiDuplicateLookupResult.NotDuplicate();
            else if (TryGetCachedDuplicateLookup(expression, settingsGeneration, out var cached))
                results[expression] = cached;
        }

        missing = expressions
            .Where(expression => !results.ContainsKey(expression))
            .ToArray();
        return results;
    }

    private IReadOnlyDictionary<string, AnkiDuplicateLookupResult> BuildCurrentSavedDuplicateLookupResults(
        IReadOnlyList<string> expressions)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var settingsGeneration = Volatile.Read(ref _settingsGeneration);
            var results = expressions.ToDictionary(
                expression => expression,
                expression => string.IsNullOrWhiteSpace(expression)
                    ? AnkiDuplicateLookupResult.NotDuplicate()
                    : SavedDuplicateLookup(expression, settingsGeneration),
                StringComparer.Ordinal);
            if (settingsGeneration == Volatile.Read(ref _settingsGeneration))
                return results;
        }

        return expressions.ToDictionary(
            expression => expression,
            _ => AnkiDuplicateLookupResult.NotDuplicate(),
            StringComparer.Ordinal);
    }

    private bool TryGetCachedDuplicateLookup(
        string expression,
        long settingsGeneration,
        out AnkiDuplicateLookupResult result)
    {
        result = AnkiDuplicateLookupResult.NotDuplicate();
        if (!_duplicateLookupCache.TryGetValue(expression, out var cached))
            return false;
        if (cached.SettingsGeneration != settingsGeneration
            || cached.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            if (cached.SettingsGeneration <= settingsGeneration
                || cached.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _duplicateLookupCache.TryRemove(
                    new KeyValuePair<string, CachedDuplicateLookup>(expression, cached));
            }
            return false;
        }

        result = cached.Result;
        return true;
    }

    private void CacheDuplicateLookup(
        string expression,
        AnkiDuplicateLookupResult result,
        long settingsGeneration)
    {
        if (settingsGeneration != Volatile.Read(ref _settingsGeneration))
            return;

        if (_duplicateLookupCache.Count >= MaxDuplicateLookupCacheEntries)
        {
            var oldest = _duplicateLookupCache
                .OrderBy(pair => pair.Value.ExpiresAt)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _duplicateLookupCache.TryRemove(
                    new KeyValuePair<string, CachedDuplicateLookup>(oldest.Key, oldest.Value));
            }
        }

        var duration = result.IsDuplicate
            ? DuplicateCacheDuration
            : NotDuplicateCacheDuration;
        var cached = new CachedDuplicateLookup(
            result,
            DateTimeOffset.UtcNow + duration,
            settingsGeneration);
        _duplicateLookupCache.AddOrUpdate(
            expression,
            cached,
            (_, existing) => existing.SettingsGeneration > settingsGeneration ? existing : cached);
    }

    private void SaveDuplicateLookup(
        string expression,
        IReadOnlyList<long> noteIds,
        long settingsGeneration)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return;

        var distinctNoteIds = noteIds
            .Where(noteId => noteId > 0)
            .Distinct()
            .ToArray();
        var saved = new SavedDuplicateLookupEntry(distinctNoteIds, settingsGeneration);
        _savedDuplicateLookups.AddOrUpdate(
            expression,
            saved,
            (_, existing) =>
            {
                if (existing.SettingsGeneration > settingsGeneration)
                    return existing;
                if (existing.SettingsGeneration < settingsGeneration)
                    return saved;

                return new SavedDuplicateLookupEntry(
                    existing.NoteIds.Concat(distinctNoteIds).Distinct().ToArray(),
                    settingsGeneration);
            });
    }

    private AnkiDuplicateLookupResult SavedDuplicateLookup(
        string expression,
        long settingsGeneration)
    {
        return _savedDuplicateLookups.TryGetValue(expression, out var saved)
               && saved.SettingsGeneration == settingsGeneration
            ? AnkiDuplicateLookupResult.Duplicate(saved.NoteIds)
            : AnkiDuplicateLookupResult.NotDuplicate();
    }

    internal static string BuildDuplicateSearchQuery(
        string expression,
        AnkiDeck deck,
        AnkiNoteType noteType,
        AnkiSettings settings)
    {
        var terms = new List<string>();
        if (settings.DuplicateScope == AnkiDuplicateScope.Deck)
        {
            terms.Add(QuoteAnkiSearchTerm($"deck:{deck.Name}"));
        }
        else if (settings.DuplicateScope == AnkiDuplicateScope.DeckRoot)
        {
            var rootDeck = deck.Name.Split("::", 2, StringSplitOptions.None)[0];
            terms.Add(QuoteAnkiSearchTerm($"deck:{rootDeck}"));
        }

        if (!settings.CheckDuplicatesAcrossAllModels)
            terms.Add(QuoteAnkiSearchTerm($"note:{noteType.Name}"));

        var firstFields = settings.CheckDuplicatesAcrossAllModels
            ? settings.AvailableNoteTypes
                .Select(candidate => candidate.Fields.FirstOrDefault())
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [noteType.Fields.First()];
        var fieldTerms = firstFields
            .Select(field => QuoteAnkiSearchTerm($"{field.ToLowerInvariant()}:{expression}"))
            .ToArray();
        if (fieldTerms.Length == 1)
            terms.Add(fieldTerms[0]);
        else if (fieldTerms.Length > 1)
            terms.Add($"({string.Join(" or ", fieldTerms)})");

        return string.Join(' ', terms);
    }

    private static string QuoteAnkiSearchTerm(string term) =>
        $"\"{term.Replace("\"", "", StringComparison.Ordinal)}\"";

    public Task<string?> GetWritableMediaDirectoryAsync()
    {
        lock (_clientLock)
            return _writableMediaDirectoryTask ??= ResolveWritableMediaDirectoryAsync();
    }

    private async Task<string?> ResolveWritableMediaDirectoryAsync()
    {
        try
        {
            var mediaDirectory = await GetClient().GetMediaDirPathAsync();
            if (!IsWritableDirectory(mediaDirectory))
                return null;

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

    private AnkiDeck? ResolveDeck() => ResolveDeck(_settings);

    private static AnkiDeck? ResolveDeck(AnkiSettings settings)
    {
        var decks = settings.AvailableDecks;
        if (decks.Count == 0) return null;

        return decks.FirstOrDefault(d => d.Id == settings.SelectedDeckId)
               ?? decks.FirstOrDefault(d => d.Name == settings.SelectedDeckName);
    }

    private AnkiNoteType? ResolveNoteType() => ResolveNoteType(_settings);

    private static AnkiNoteType? ResolveNoteType(AnkiSettings settings)
    {
        var noteTypes = settings.AvailableNoteTypes;
        if (noteTypes.Count == 0) return null;

        return noteTypes.FirstOrDefault(nt => nt.Id == settings.SelectedNoteTypeId)
               ?? noteTypes.FirstOrDefault(nt => nt.Name == settings.SelectedNoteTypeName);
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _settingsService.SettingChanged -= SettingsService_SettingChanged;
        lock (_clientLock)
        {
            _client?.Dispose();
            _client = null;
            _writableMediaDirectoryTask = null;
        }
        _savedDuplicateLookups.Clear();
        _duplicateLookupCache.Clear();
        _duplicateLookupGate.Dispose();
    }
}
