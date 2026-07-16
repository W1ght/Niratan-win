using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Models.Profiles;
using Niratan.Models.Sync;
using Niratan.Services.Dictionary;
using Niratan.Services.Novels;
using Niratan.Services.Profiles;
using Niratan.Services.Sync;

namespace Niratan.Services.Backup;

public sealed class BackupService : IBackupService
{
    private const int MaximumArchiveEntries = 250_000;
    private const long MaximumExtractedBytes = 512L * 1024 * 1024 * 1024;
    private const long ReservedFreeSpaceBytes = 512L * 1024 * 1024;
    internal const string ProfileMetadataDirectoryName = ".hoshi-profiles";
    private const string ProfilesIndexFileName = "profiles.json";
    private const string DictionarySettingsFileName = "dictionary-settings.json";
    private const string DictionaryConfigDirectoryName = "dictionaries";
    private const string DictionaryConfigFileName = "dictionary-config.json";

    private static readonly JsonSerializerOptions ProfileJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions TtuJsonOptions = CreateTtuJsonOptions();

    private readonly ILogger<BackupService> _logger;
    private readonly string _booksRoot;
    private readonly string _dictionaryRoot;
    private readonly string _profilesRoot;
    private readonly DictionaryLookupService? _dictionaryLookup;
    private readonly IProfileService? _profiles;
    private readonly ProfileRuntimeService? _profileRuntime;
    private readonly INovelLibraryService? _novelLibrary;
    private readonly INovelBookSidecarService? _bookSidecars;
    private readonly INovelStatisticsSidecarService? _statisticsSidecars;
    private readonly ITtuBackupBookDataConverter? _ttuConverter;

    public BackupService(
        ILogger<BackupService> logger,
        DictionaryLookupService dictionaryLookup,
        IProfileService profiles,
        ProfileRuntimeService profileRuntime,
        INovelLibraryService novelLibrary,
        INovelBookSidecarService bookSidecars,
        INovelStatisticsSidecarService statisticsSidecars,
        ITtuBackupBookDataConverter ttuConverter)
        : this(
            logger,
            AppDataHelper.GetNovelBooksPath(),
            Path.Combine(AppDataHelper.GetAppDataPath(), "dictionaries"),
            profiles.ProfilesRoot,
            dictionaryLookup,
            profiles,
            profileRuntime,
            novelLibrary,
            bookSidecars,
            statisticsSidecars,
            ttuConverter)
    {
    }

    internal BackupService(
        ILogger<BackupService> logger,
        string booksRoot,
        string dictionaryRoot,
        string profilesRoot,
        DictionaryLookupService? dictionaryLookup = null,
        IProfileService? profiles = null,
        ProfileRuntimeService? profileRuntime = null,
        INovelLibraryService? novelLibrary = null,
        INovelBookSidecarService? bookSidecars = null,
        INovelStatisticsSidecarService? statisticsSidecars = null,
        ITtuBackupBookDataConverter? ttuConverter = null)
    {
        _logger = logger;
        _booksRoot = Path.GetFullPath(booksRoot);
        _dictionaryRoot = Path.GetFullPath(dictionaryRoot);
        _profilesRoot = Path.GetFullPath(profilesRoot);
        _dictionaryLookup = dictionaryLookup;
        _profiles = profiles;
        _profileRuntime = profileRuntime;
        _novelLibrary = novelLibrary;
        _bookSidecars = bookSidecars;
        _statisticsSidecars = statisticsSidecars;
        _ttuConverter = ttuConverter;
    }

    public async Task CreateHoshiBackupAsync(
        HoshiBackupTarget target,
        string destinationPath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var source = target == HoshiBackupTarget.Books ? _booksRoot : _dictionaryRoot;
        if (!Directory.Exists(source))
            throw new DirectoryNotFoundException($"The {target.ToString().ToLowerInvariant()} collection does not exist.");

        var staging = target == HoshiBackupTarget.Dictionaries
            ? CreateTemporaryDirectory("niratan-dictionary-backup")
            : null;
        try
        {
            if (staging != null)
            {
                await CreateDictionaryStagingAsync(staging, ct).ConfigureAwait(false);
                source = staging;
            }

            await Task.Run(
                () => CreateArchiveAtomically(source, destinationPath, ct),
                ct).ConfigureAwait(false);
            _logger.LogInformation("Created {Target} backup at {DestinationPath}", target, destinationPath);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    public async Task RestoreHoshiBackupAsync(
        HoshiBackupTarget target,
        string archivePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!File.Exists(archivePath))
            throw new FileNotFoundException("The selected backup does not exist.", archivePath);

        var extracted = CreateTemporaryDirectory("niratan-restore");
        try
        {
            await Task.Run(
                () => ExtractArchiveSafely(archivePath, extracted, ct),
                ct).ConfigureAwait(false);
            var payload = ResolvePayloadRoot(extracted, target);

            if (target == HoshiBackupTarget.Books)
            {
                await Task.Run(
                    () => ReplaceDirectoryAtomically(payload, _booksRoot, excludedName: null, ct),
                    ct).ConfigureAwait(false);
            }
            else
            {
                await RestoreDictionariesAsync(payload, ct).ConfigureAwait(false);
            }

            _logger.LogInformation("Restored {Target} backup from {ArchivePath}", target, archivePath);
        }
        finally
        {
            TryDeleteDirectory(extracted);
        }
    }

    public async Task ExportTtuBackupAsync(
        string destinationPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var library = Require(_novelLibrary);
        var sidecars = Require(_bookSidecars);
        var statisticsSidecars = Require(_statisticsSidecars);
        var converter = Require(_ttuConverter);
        var snapshotResult = await library.GetNovelBooksAsync(ct: ct).ConfigureAwait(false);
        if (!snapshotResult.IsSuccess || snapshotResult.Value == null)
            throw new InvalidOperationException(snapshotResult.Error ?? "Could not load the book collection.");

        var books = snapshotResult.Value.Books
            .Where(book => !string.IsNullOrWhiteSpace(book.FilePath) && File.Exists(book.FilePath))
            .ToList();
        var temporaryArchive = GetTemporarySiblingPath(destinationPath);
        var workRoot = CreateTemporaryDirectory("niratan-ttu-export");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            using (var archive = ZipFile.Open(temporaryArchive, ZipArchiveMode.Create))
            {
                var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var index = 0; index < books.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var book = books[index];
                    progress?.Report($"{index + 1}/{books.Count}");
                    var bookWork = Path.Combine(workRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(bookWork);
                    var bookDataPath = await converter.ConvertFromEpubAsync(book, bookWork, ct)
                        .ConfigureAwait(false);
                    var folderName = MakeUniqueArchiveFolder(
                        TtuSyncFileNames.SanitizeTtuFilename(book.Title.Normalize()),
                        usedFolders);
                    AddFile(archive, bookDataPath, $"{folderName}/{Path.GetFileName(bookDataPath)}", CompressionLevel.SmallestSize);

                    if (!string.IsNullOrWhiteSpace(book.CoverPath) && File.Exists(book.CoverPath))
                    {
                        AddFile(
                            archive,
                            book.CoverPath,
                            $"{folderName}/cover_1_6{Path.GetExtension(book.CoverPath)}",
                            CompressionLevel.SmallestSize);
                    }

                    if (!string.IsNullOrWhiteSpace(book.ExtractedPath))
                    {
                        var statistics = await statisticsSidecars.LoadAsync(book.ExtractedPath, ct)
                            .ConfigureAwait(false);
                        if (statistics.Count > 0)
                        {
                            AddJson(
                                archive,
                                $"{folderName}/{TtuSyncFileNames.GetStatisticsFileName(statistics)}",
                                statistics);
                        }

                        var bookmark = await sidecars.LoadBookmarkAsync(book.ExtractedPath, ct)
                            .ConfigureAwait(false);
                        var bookInfo = await sidecars.LoadBookInfoAsync(book.ExtractedPath, ct)
                            .ConfigureAwait(false);
                        if (bookmark != null && bookInfo != null)
                        {
                            var modified = bookmark.LastModified
                                ?? new DateTimeOffset(book.LastOpenedAt ?? book.ImportedAt, TimeSpan.Zero);
                            modified = DateTimeOffset.FromUnixTimeMilliseconds(modified.ToUnixTimeMilliseconds());
                            var ttuProgress = new TtuProgress(
                                0,
                                bookmark.CharacterCount,
                                bookInfo.CharacterCount > 0
                                    ? bookmark.CharacterCount / (double)bookInfo.CharacterCount
                                    : 0,
                                modified);
                            AddJson(
                                archive,
                                $"{folderName}/{TtuSyncFileNames.GetProgressFileName(ttuProgress)}",
                                ttuProgress);
                        }
                    }
                }
            }

            File.Move(temporaryArchive, destinationPath, overwrite: true);
            _logger.LogInformation("Exported TTU backup with {BookCount} books to {DestinationPath}", books.Count, destinationPath);
        }
        finally
        {
            TryDeleteFile(temporaryArchive);
            TryDeleteDirectory(workRoot);
        }
    }

    public async Task<TtuBackupImportResult> ImportTtuBackupAsync(
        string archivePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var library = Require(_novelLibrary);
        var sidecars = Require(_bookSidecars);
        var statisticsSidecars = Require(_statisticsSidecars);
        var converter = Require(_ttuConverter);
        var extracted = CreateTemporaryDirectory("niratan-ttu-import");
        try
        {
            await Task.Run(() => ExtractArchiveSafely(archivePath, extracted, ct), ct).ConfigureAwait(false);
            var folders = Directory.EnumerateDirectories(extracted).ToList();
            var snapshotResult = await library.GetNovelBooksAsync(ct: ct).ConfigureAwait(false);
            if (!snapshotResult.IsSuccess || snapshotResult.Value == null)
                throw new InvalidOperationException(snapshotResult.Error ?? "Could not load the book collection.");

            var knownBooks = snapshotResult.Value.Books.ToList();
            var added = 0;
            var updated = 0;
            for (var index = 0; index < folders.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"{index + 1}/{folders.Count}");
                var folder = folders[index];
                var files = Directory.EnumerateFiles(folder).ToList();
                var bookDataPath = files.FirstOrDefault(path =>
                    Path.GetFileName(path).StartsWith("bookdata_", StringComparison.Ordinal)
                    && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase));
                if (bookDataPath == null)
                    continue;

                var title = await converter.ReadTitleAsync(bookDataPath, ct).ConfigureAwait(false);
                var book = knownBooks.FirstOrDefault(candidate =>
                    string.Equals(candidate.OriginalTitle, title, StringComparison.Ordinal)
                    || string.Equals(candidate.Title, title, StringComparison.Ordinal));
                if (book == null)
                {
                    var conversionRoot = Path.Combine(extracted, ".converted", Guid.NewGuid().ToString("N"));
                    var epubPath = await converter.ConvertToEpubAsync(bookDataPath, conversionRoot, ct)
                        .ConfigureAwait(false);
                    var importResult = await library.ImportEpubAsync(epubPath, ct).ConfigureAwait(false);
                    if (!importResult.IsSuccess || importResult.Value == null)
                        throw new InvalidOperationException(importResult.Error ?? $"Could not import '{title}'.");
                    book = importResult.Value;
                    knownBooks.Add(book);
                    added++;
                }
                else
                {
                    updated++;
                }

                if (string.IsNullOrWhiteSpace(book.ExtractedPath))
                    continue;
                var statisticsPath = files.FirstOrDefault(path =>
                    Path.GetFileName(path).StartsWith("statistics_", StringComparison.Ordinal));
                if (statisticsPath != null)
                {
                    await using var stream = File.OpenRead(statisticsPath);
                    var statistics = await JsonSerializer.DeserializeAsync<List<NovelReadingStatistic>>(
                        stream,
                        TtuJsonOptions,
                        ct).ConfigureAwait(false);
                    if (statistics != null)
                        await statisticsSidecars.SaveAsync(book.ExtractedPath, statistics, ct).ConfigureAwait(false);
                }

                var progressPath = files.FirstOrDefault(path =>
                    Path.GetFileName(path).StartsWith("progress_", StringComparison.Ordinal));
                if (progressPath != null)
                {
                    await using var stream = File.OpenRead(progressPath);
                    var ttuProgress = await JsonSerializer.DeserializeAsync<TtuProgress>(
                        stream,
                        TtuJsonOptions,
                        ct).ConfigureAwait(false);
                    if (ttuProgress != null)
                    {
                        var bookInfo = await sidecars.LoadBookInfoAsync(book.ExtractedPath, ct)
                            .ConfigureAwait(false);
                        var position = ResolveCharacterPosition(bookInfo, ttuProgress);
                        await sidecars.SaveBookmarkAsync(
                            book.ExtractedPath,
                            new NovelBookmark(
                                position.ChapterIndex,
                                position.Progress,
                                ttuProgress.ExploredCharCount,
                                ttuProgress.LastBookmarkModified),
                            ct).ConfigureAwait(false);
                    }
                }
            }

            _logger.LogInformation(
                "Imported TTU backup from {ArchivePath}: {AddedBooks} added, {UpdatedBooks} updated",
                archivePath,
                added,
                updated);
            return new TtuBackupImportResult(added, updated);
        }
        finally
        {
            TryDeleteDirectory(extracted);
        }
    }

    private async Task CreateDictionaryStagingAsync(string staging, CancellationToken ct)
    {
        await Task.Run(() => CopyDirectoryContents(_dictionaryRoot, staging, excludedName: ProfileMetadataDirectoryName, ct), ct)
            .ConfigureAwait(false);

        var indexPath = Path.Combine(_profilesRoot, ProfilesIndexFileName);
        if (!File.Exists(indexPath))
            return;
        var index = await ReadProfileIndexAsync(indexPath, ct).ConfigureAwait(false);
        var metadataRoot = Path.Combine(staging, ProfileMetadataDirectoryName);
        Directory.CreateDirectory(metadataRoot);
        await WriteJsonAtomicallyAsync(
            Path.Combine(metadataRoot, ProfilesIndexFileName),
            index,
            ProfileJsonOptions,
            ct).ConfigureAwait(false);

        foreach (var profile in index.Profiles.Where(profile => IsSafePathSegment(profile.Id)))
        {
            ct.ThrowIfCancellationRequested();
            var sourceProfile = Path.Combine(_profilesRoot, profile.Id);
            var outputProfile = Path.Combine(metadataRoot, profile.Id);
            Directory.CreateDirectory(outputProfile);
            CopyIfPresent(
                Path.Combine(sourceProfile, DictionarySettingsFileName),
                Path.Combine(outputProfile, DictionarySettingsFileName));
            CopyIfPresent(
                Path.Combine(sourceProfile, DictionaryConfigDirectoryName, DictionaryConfigFileName),
                Path.Combine(outputProfile, DictionaryConfigDirectoryName, DictionaryConfigFileName));
        }

        var defaultConfig = Path.Combine(
            _profilesRoot,
            index.DefaultProfileId,
            DictionaryConfigDirectoryName,
            DictionaryConfigFileName);
        CopyIfPresent(defaultConfig, Path.Combine(staging, DictionaryConfigFileName));
    }

    private async Task RestoreDictionariesAsync(string payload, CancellationToken ct)
    {
        ValidateDictionaryPayload(payload);
        var replacement = GetReplacementPath(_dictionaryRoot);
        var previous = GetPreviousPath(_dictionaryRoot);
        var profilesReplacement = GetReplacementPath(_profilesRoot);
        var profilesPrevious = GetPreviousPath(_profilesRoot);
        var dictionariesCommitted = false;
        var profilesCommitted = false;
        try
        {
            CopyDirectoryContents(payload, replacement, ProfileMetadataDirectoryName, ct);
            await PrepareMergedProfilesAsync(payload, profilesReplacement, ct).ConfigureAwait(false);
            if (_dictionaryLookup != null)
                await _dictionaryLookup.SuspendForCollectionReplacementAsync().ConfigureAwait(false);

            ReplacePreparedDirectory(replacement, _dictionaryRoot, previous);
            dictionariesCommitted = true;
            ReplacePreparedDirectory(profilesReplacement, _profilesRoot, profilesPrevious);
            profilesCommitted = true;

            if (_profiles != null)
                await _profiles.LoadAsync().ConfigureAwait(false);
            if (_profileRuntime != null)
                await _profileRuntime.ReloadActiveProfileAsync(ct).ConfigureAwait(false);
            else if (_dictionaryLookup != null)
                await _dictionaryLookup.RebuildQueryAsync().ConfigureAwait(false);

            TryDeleteDirectory(previous);
            TryDeleteDirectory(profilesPrevious);
        }
        catch
        {
            if (profilesCommitted)
                RestorePreviousDirectory(_profilesRoot, profilesPrevious);
            if (dictionariesCommitted)
                RestorePreviousDirectory(_dictionaryRoot, previous);
            try
            {
                if (_profiles != null)
                    await _profiles.LoadAsync().ConfigureAwait(false);
                if (_profileRuntime != null)
                    await _profileRuntime.ReloadActiveProfileAsync(CancellationToken.None).ConfigureAwait(false);
                else if (_dictionaryLookup != null)
                    await _dictionaryLookup.RebuildQueryAsync().ConfigureAwait(false);
            }
            catch (Exception recoveryError)
            {
                _logger.LogWarning(recoveryError, "Failed to reload the previous dictionary collection after rollback");
            }
            throw;
        }
        finally
        {
            TryDeleteDirectory(replacement);
            TryDeleteDirectory(previous);
            TryDeleteDirectory(profilesReplacement);
            TryDeleteDirectory(profilesPrevious);
        }
    }

    private async Task PrepareMergedProfilesAsync(
        string payload,
        string prepared,
        CancellationToken ct)
    {
        TryDeleteDirectory(prepared);
        if (Directory.Exists(_profilesRoot))
            CopyDirectoryContents(_profilesRoot, prepared, excludedName: null, ct);
        else
            Directory.CreateDirectory(prepared);

        var currentIndexPath = Path.Combine(prepared, ProfilesIndexFileName);
        var current = File.Exists(currentIndexPath)
            ? await ReadProfileIndexAsync(currentIndexPath, ct).ConfigureAwait(false)
            : ProfileIndex.CreateDefault();
        var metadataRoot = Path.Combine(payload, ProfileMetadataDirectoryName);
        var importedIndexPath = Path.Combine(metadataRoot, ProfilesIndexFileName);
        if (File.Exists(importedIndexPath))
        {
            var imported = await ReadProfileIndexAsync(importedIndexPath, ct).ConfigureAwait(false);
            var profilesById = current.Profiles.ToDictionary(profile => profile.Id, StringComparer.Ordinal);
            foreach (var importedProfile in imported.Profiles)
            {
                if (!IsSafePathSegment(importedProfile.Id))
                    throw new InvalidDataException("The backup contains an invalid Profile index.");
                profilesById[importedProfile.Id] = importedProfile;
                var sourceProfile = Path.Combine(metadataRoot, importedProfile.Id);
                var destinationProfile = Path.Combine(prepared, importedProfile.Id);
                Directory.CreateDirectory(destinationProfile);
                CopyIfPresent(
                    Path.Combine(sourceProfile, DictionarySettingsFileName),
                    Path.Combine(destinationProfile, DictionarySettingsFileName));
                CopyIfPresent(
                    Path.Combine(sourceProfile, DictionaryConfigDirectoryName, DictionaryConfigFileName),
                    Path.Combine(destinationProfile, DictionaryConfigDirectoryName, DictionaryConfigFileName));
            }

            current.Profiles = profilesById.Values
                .OrderBy(profile => profile.Id == current.DefaultProfileId ? 0 : 1)
                .ThenBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            foreach (var (language, profileId) in imported.PrimaryProfileIdsByLanguage)
            {
                if (profilesById.ContainsKey(profileId))
                    current.PrimaryProfileIdsByLanguage[language] = profileId;
            }
        }
        else
        {
            var legacyConfig = Path.Combine(payload, DictionaryConfigFileName);
            if (File.Exists(legacyConfig) && IsSafePathSegment(current.DefaultProfileId))
            {
                CopyIfPresent(
                    legacyConfig,
                    Path.Combine(
                        prepared,
                        current.DefaultProfileId,
                        DictionaryConfigDirectoryName,
                        DictionaryConfigFileName));
            }
        }

        await WriteJsonAtomicallyAsync(currentIndexPath, current, ProfileJsonOptions, ct).ConfigureAwait(false);
    }

    private static void ValidateDictionaryPayload(string root)
    {
        var hasPayload = File.Exists(Path.Combine(root, DictionaryConfigFileName))
            || Directory.Exists(Path.Combine(root, ProfileMetadataDirectoryName));
        foreach (var type in new[] { "Term", "Frequency", "Pitch" })
        {
            var typeRoot = Path.Combine(root, type);
            if (!Directory.Exists(typeRoot))
                continue;
            hasPayload = true;
            foreach (var dictionary in Directory.EnumerateDirectories(typeRoot))
            {
                var indexPath = Path.Combine(dictionary, "index.json");
                if (!File.Exists(indexPath))
                    throw new InvalidDataException("The backup does not contain a valid dictionary collection.");
                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
                    if (document.RootElement.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("The backup does not contain a valid dictionary collection.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("The backup does not contain a valid dictionary collection.", ex);
                }
            }
        }

        if (!hasPayload)
            throw new InvalidDataException("The backup does not contain a dictionary collection.");
    }

    private static string ResolvePayloadRoot(string extracted, HoshiBackupTarget target)
    {
        var entries = Directory.EnumerateFileSystemEntries(extracted).ToList();
        if (target == HoshiBackupTarget.Dictionaries && LooksLikeDictionaryPayload(extracted))
            return extracted;
        if (target == HoshiBackupTarget.Books && LooksLikeBooksPayload(extracted))
            return extracted;
        if (entries.Count == 1 && Directory.Exists(entries[0]))
            return entries[0];
        if (target == HoshiBackupTarget.Books)
            return extracted;
        throw new InvalidDataException("The backup does not contain a dictionary collection.");
    }

    private static bool LooksLikeDictionaryPayload(string root) =>
        new[] { "Term", "Frequency", "Pitch", DictionaryConfigFileName, ProfileMetadataDirectoryName }
            .Any(name => Directory.Exists(Path.Combine(root, name)) || File.Exists(Path.Combine(root, name)));

    private static bool LooksLikeBooksPayload(string root) =>
        File.Exists(Path.Combine(root, "book_order.json"))
        || Directory.EnumerateDirectories(root).Any(directory => File.Exists(Path.Combine(directory, "metadata.json")));

    private static void CreateArchiveAtomically(string sourceRoot, string destinationPath, CancellationToken ct)
    {
        var temporaryPath = GetTemporarySiblingPath(destinationPath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
            using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
                    AddFile(archive, file, relative, CompressionLevel.SmallestSize);
                }
            }
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    internal static void ExtractArchiveSafely(string archivePath, string destinationRoot, CancellationToken ct)
    {
        Directory.CreateDirectory(destinationRoot);
        var canonicalRoot = Path.GetFullPath(destinationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumArchiveEntries)
            throw new InvalidDataException("The backup contains too many file entries.");
        var driveRoot = Path.GetPathRoot(Path.GetFullPath(destinationRoot));
        var availableBytes = string.IsNullOrWhiteSpace(driveRoot)
            ? MaximumExtractedBytes
            : Math.Max(0, new DriveInfo(driveRoot).AvailableFreeSpace - ReservedFreeSpaceBytes);
        var extractionLimit = Math.Min(MaximumExtractedBytes, availableBytes);
        long extractedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                extractedBytes = checked(extractedBytes + entry.Length);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("The backup is too large.", ex);
            }
            if (extractedBytes > extractionLimit)
                throw new InvalidDataException("The backup is too large.");
            if (IsSymbolicLink(entry))
                throw new InvalidDataException("The backup contains an unsafe file entry.");
            var normalizedName = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(normalizedName))
                throw new InvalidDataException("The backup contains an unsafe file entry.");
            var destination = Path.GetFullPath(Path.Combine(destinationRoot, normalizedName));
            if (!destination.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The backup contains an unsafe file entry.");
            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static void ReplaceDirectoryAtomically(
        string payload,
        string destination,
        string? excludedName,
        CancellationToken ct)
    {
        var replacement = GetReplacementPath(destination);
        var previous = GetPreviousPath(destination);
        try
        {
            CopyDirectoryContents(payload, replacement, excludedName, ct);
            ReplacePreparedDirectory(replacement, destination, previous);
            TryDeleteDirectory(previous);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(previous))
                Directory.Move(previous, destination);
            throw;
        }
        finally
        {
            TryDeleteDirectory(replacement);
            TryDeleteDirectory(previous);
        }
    }

    private static void ReplacePreparedDirectory(string replacement, string destination, string previous)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        TryDeleteDirectory(previous);
        if (Directory.Exists(destination))
            Directory.Move(destination, previous);
        try
        {
            Directory.Move(replacement, destination);
        }
        catch
        {
            if (!Directory.Exists(destination) && Directory.Exists(previous))
                Directory.Move(previous, destination);
            throw;
        }
    }

    private static void RestorePreviousDirectory(string destination, string previous)
    {
        if (!Directory.Exists(previous))
            return;
        TryDeleteDirectory(destination);
        Directory.Move(previous, destination);
    }

    private static void CopyDirectoryContents(
        string source,
        string destination,
        string? excludedName,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, directory);
            if (IsExcluded(relative, excludedName))
                continue;
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            if (IsExcluded(relative, excludedName))
                continue;
            var output = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file, output, overwrite: true);
        }
    }

    private static bool IsExcluded(string relativePath, string? excludedName) =>
        excludedName != null
        && relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]
            .Equals(excludedName, StringComparison.Ordinal);

    private static async Task<ProfileIndex> ReadProfileIndexAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            var index = await JsonSerializer.DeserializeAsync<ProfileIndex>(stream, ProfileJsonOptions, ct)
                .ConfigureAwait(false);
            if (index == null || index.Profiles.Any(profile => !IsSafePathSegment(profile.Id)))
                throw new InvalidDataException("The backup contains an invalid Profile index.");
            return index;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The backup contains an invalid Profile index.", ex);
        }
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        JsonSerializerOptions options,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, options, ct).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static (int ChapterIndex, double Progress) ResolveCharacterPosition(
        NovelBookInfo? bookInfo,
        TtuProgress progress)
    {
        if (bookInfo == null || bookInfo.ChapterInfo.Count == 0)
            return (0, Math.Clamp(progress.Progress, 0, 1));
        var target = Math.Clamp(progress.ExploredCharCount, 0, bookInfo.CharacterCount);
        var chapters = bookInfo.ChapterInfo.Values
            .Where(chapter => chapter.SpineIndex.HasValue)
            .OrderBy(chapter => chapter.CurrentTotal)
            .ThenBy(chapter => chapter.SpineIndex)
            .ToList();
        if (chapters.Count == 0)
            return (0, Math.Clamp(progress.Progress, 0, 1));
        var chapter = chapters.LastOrDefault(item => target >= item.CurrentTotal) ?? chapters[0];
        var read = Math.Clamp(target - chapter.CurrentTotal, 0, chapter.ChapterCount);
        return (
            chapter.SpineIndex!.Value,
            chapter.ChapterCount > 0 ? read / (double)chapter.ChapterCount : 0);
    }

    private static JsonSerializerOptions CreateTtuJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new UnixMillisecondsDateTimeOffsetConverter());
        return options;
    }

    private sealed class UnixMillisecondsDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Number when reader.TryGetInt64(out var value) =>
                    DateTimeOffset.FromUnixTimeMilliseconds(value),
                JsonTokenType.String when DateTimeOffset.TryParse(
                    reader.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal,
                    out var value) => value,
                _ => throw new JsonException("Expected a Unix millisecond timestamp."),
            };

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options) =>
            writer.WriteNumberValue(value.ToUnixTimeMilliseconds());
    }

    private static void AddJson<T>(ZipArchive archive, string path, T value)
    {
        var entry = archive.CreateEntry(path.Replace('\\', '/'), CompressionLevel.SmallestSize);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, TtuJsonOptions);
    }

    private static void AddFile(ZipArchive archive, string sourcePath, string archivePath, CompressionLevel compression)
    {
        var entry = archive.CreateEntry(archivePath.Replace('\\', '/'), compression);
        using var source = File.OpenRead(sourcePath);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private static string MakeUniqueArchiveFolder(string suggested, ISet<string> used)
    {
        var safe = string.IsNullOrWhiteSpace(suggested) ? "Book" : suggested;
        var candidate = safe;
        var suffix = 2;
        while (!used.Add(candidate))
            candidate = $"{safe} ({suffix++})";
        return candidate;
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(
            AppDataHelper.GetTemporaryDataPath(),
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string GetTemporarySiblingPath(string destinationPath) =>
        Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(destinationPath))!,
            $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

    private static string GetReplacementPath(string destination) =>
        Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.restore-{Guid.NewGuid():N}");

    private static string GetPreviousPath(string destination) =>
        Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.previous-{Guid.NewGuid():N}");

    private static bool IsSafePathSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
        && !Path.IsPathFullyQualified(value);

    private static void CopyIfPresent(string source, string destination)
    {
        if (!File.Exists(source))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static T Require<T>(T? service) where T : class =>
        service ?? throw new InvalidOperationException($"{typeof(T).Name} is unavailable.");

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
