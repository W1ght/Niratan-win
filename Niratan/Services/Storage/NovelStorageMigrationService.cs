using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Services.Novels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Niratan.Services.Storage;

internal interface INovelStorageMigrationService
{
    Task<NovelStorageMigrationResult> MigrateAsync(CancellationToken ct = default);
}

internal sealed class NovelStorageMigrationService : INovelStorageMigrationService
{
    private const int ManifestVersion = 1;
    private const string MetadataFileName = "metadata.json";
    private const string BookmarkFileName = "bookmark.json";
    private const string BookInfoFileName = "bookinfo.json";
    private const string BookOrderFileName = "book_order.json";
    private const string ShelvesFileName = "shelves.json";
    private const string ManifestFileName = "novel_storage_migration_v1.json";
    private const string BackupSuffix = ".pre-novel-files-v1.bak";

    private readonly string _connectionString;
    private readonly string _booksRoot;
    private readonly string _manifestPath;
    private readonly INovelBookStorageService _storage;
    private readonly INovelBookSidecarService _sidecars;
    private readonly INiratanJsonFileStore _json;
    private readonly ILogger<NovelStorageMigrationService> _logger;

    public NovelStorageMigrationService(
        INovelBookStorageService storage,
        INovelBookSidecarService sidecars,
        INiratanJsonFileStore json,
        ILogger<NovelStorageMigrationService> logger)
        : this(
            $"Data Source={Path.Combine(AppDataHelper.GetDataPath(), "niratan.db")}",
            AppDataHelper.GetNovelBooksPath(),
            storage,
            sidecars,
            json,
            logger)
    {
    }

    internal NovelStorageMigrationService(
        string connectionString,
        string booksRoot,
        INovelBookStorageService storage,
        INovelBookSidecarService sidecars,
        INiratanJsonFileStore json,
        ILogger<NovelStorageMigrationService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(booksRoot);
        _connectionString = connectionString;
        _booksRoot = Path.GetFullPath(booksRoot);
        _manifestPath = Path.Combine(_booksRoot, ManifestFileName);
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _sidecars = sidecars ?? throw new ArgumentNullException(nameof(sidecars));
        _json = json ?? throw new ArgumentNullException(nameof(json));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        Directory.CreateDirectory(_booksRoot);
    }

    public async Task<NovelStorageMigrationResult> MigrateAsync(
        CancellationToken ct = default)
    {
        try
        {
            if (!await LegacyTableExistsAsync(ct))
                return await CompleteWithoutLegacyTableAsync(ct);

            CreateBackup();
            var rows = await LoadLegacyBooksAsync(ct);
            var manifestBooks = new List<NovelStorageMigrationBook>(rows.Count);
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                manifestBooks.Add(await ExportBookAsync(row, ct));
            }

            await _storage.SaveBookOrderAsync(
                rows.OrderBy(row => row.ManualSortOrder).Select(row => row.Id).ToList(),
                ct);
            await EnsureEmptyArrayFileAsync(Path.Combine(_booksRoot, ShelvesFileName), ct);

            var manifest = new NovelStorageMigrationManifest(
                ManifestVersion,
                DateTimeOffset.UtcNow,
                manifestBooks);
            await ValidateManifestAsync(manifest, ct);
            await DropLegacyTablesAsync(ct);
            await _json.WriteAsync(_manifestPath, manifest, ct);

            _logger.LogInformation(
                "Migrated {BookCount} novels from SQLite to sidecar storage",
                rows.Count);
            return new NovelStorageMigrationResult(true, false, null, rows.Count);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsRecoverableMigrationFailure(ex))
        {
            _logger.LogError(
                ex,
                "Novel storage migration failed; novel writes are disabled");
            return new NovelStorageMigrationResult(false, true, ex.Message, 0);
        }
    }

    private async Task<NovelStorageMigrationResult> CompleteWithoutLegacyTableAsync(
        CancellationToken ct)
    {
        await EnsureEmptyArrayFileAsync(Path.Combine(_booksRoot, BookOrderFileName), ct);
        await EnsureEmptyArrayFileAsync(Path.Combine(_booksRoot, ShelvesFileName), ct);

        var existingManifest = await _json.ReadAsync<NovelStorageMigrationManifest>(
            _manifestPath,
            ct);
        if (existingManifest.Status == NovelJsonReadStatus.Invalid)
            throw new InvalidDataException(existingManifest.Error ?? "Invalid migration manifest.");
        if (existingManifest.Status == NovelJsonReadStatus.Success
            && existingManifest.Value is not null)
        {
            return new NovelStorageMigrationResult(
                true,
                false,
                null,
                existingManifest.Value.Books.Count);
        }

        var snapshot = await _storage.LoadSnapshotAsync(ct: ct);
        if (snapshot.CorruptMetadataPaths.Count > 0)
            throw new InvalidDataException("Novel metadata validation failed.");

        var manifest = new NovelStorageMigrationManifest(
            ManifestVersion,
            DateTimeOffset.UtcNow,
            snapshot.Books.Select(ToManifestBook).ToList());
        await _json.WriteAsync(_manifestPath, manifest, ct);
        return new NovelStorageMigrationResult(true, false, null, snapshot.Books.Count);
    }

    private async Task<NovelStorageMigrationBook> ExportBookAsync(
        LegacyNovelBookRow row,
        CancellationToken ct)
    {
        var root = ResolveBookRoot(row.Id);
        Directory.CreateDirectory(root);
        var metadataPath = Path.Combine(root, MetadataFileName);
        var metadataResult = await _json.ReadAsync<NovelBookMetadata>(metadataPath, ct);
        if (metadataResult.Status == NovelJsonReadStatus.Invalid)
            throw new InvalidDataException(metadataResult.Error ?? $"Invalid metadata for {row.Id}.");

        if (metadataResult.Status == NovelJsonReadStatus.Missing)
        {
            var privateEpubPath = CopyPrivateEpubIfAvailable(row, root);
            var book = new NovelBook
            {
                Id = row.Id,
                Title = row.Title,
                OriginalTitle = row.Title,
                Folder = Path.GetFileName(root),
                FilePath = privateEpubPath ?? string.Empty,
                CoverPath = row.CoverPath,
                ImportedAt = AsUtc(row.ImportedAt),
                LastOpenedAt = row.LastOpenedAt is null ? null : AsUtc(row.LastOpenedAt.Value),
                Language = row.Language,
                ExtractedPath = root,
                ProfileId = row.ProfileId,
            };
            await _storage.SaveMetadataAsync(book, ct);
        }

        var legacyModified = new DateTimeOffset(
            AsUtc(row.LastOpenedAt ?? row.ImportedAt));
        var bookmarkPath = Path.Combine(root, BookmarkFileName);
        var bookmarkResult = await _json.ReadAsync<NovelBookmark>(bookmarkPath, ct);
        var preserveBookmark = bookmarkResult.Status == NovelJsonReadStatus.Success
            && bookmarkResult.Value is not null
            && bookmarkResult.Value.LastModified >= legacyModified;
        if (!preserveBookmark)
        {
            await _sidecars.SaveBookmarkAsync(
                root,
                new NovelBookmark(
                    row.CurrentChapterIndex,
                    row.Progress,
                    row.CurrentCharacterCount,
                    legacyModified),
                ct);
        }

        var bookInfoPath = Path.Combine(root, BookInfoFileName);
        var bookInfoResult = await _json.ReadAsync<NovelBookInfo>(bookInfoPath, ct);
        if (bookInfoResult.Status != NovelJsonReadStatus.Success
            || bookInfoResult.Value is null)
        {
            await _sidecars.SaveBookInfoAsync(
                root,
                new NovelBookInfo(Math.Max(0, row.TotalCharacterCount), []),
                ct);
        }

        var finalMetadata = await RequireValidAsync<NovelBookMetadata>(metadataPath, ct);
        var finalBookmark = await RequireValidAsync<NovelBookmark>(bookmarkPath, ct);
        var finalBookInfo = await RequireValidAsync<NovelBookInfo>(bookInfoPath, ct);
        return new NovelStorageMigrationBook(
            finalMetadata.Id,
            finalMetadata.Folder,
            finalBookmark.ChapterIndex,
            finalBookmark.CharacterCount,
            finalBookInfo.CharacterCount,
            finalMetadata.ProfileId);
    }

    private string? CopyPrivateEpubIfAvailable(LegacyNovelBookRow row, string root)
    {
        if (string.IsNullOrWhiteSpace(row.FilePath) || !File.Exists(row.FilePath))
            return null;

        var destination = Path.Combine(root, row.Id + ".epub");
        if (!string.Equals(
                Path.GetFullPath(row.FilePath),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase)
            && !File.Exists(destination))
        {
            File.Copy(row.FilePath, destination, overwrite: false);
        }

        return destination;
    }

    private async Task<T> RequireValidAsync<T>(string path, CancellationToken ct)
        where T : class
    {
        var result = await _json.ReadAsync<T>(path, ct);
        if (result.Status != NovelJsonReadStatus.Success || result.Value is null)
            throw new InvalidDataException(result.Error ?? $"Required sidecar is invalid: {path}");
        return result.Value;
    }

    private async Task ValidateManifestAsync(
        NovelStorageMigrationManifest manifest,
        CancellationToken ct)
    {
        var snapshot = await _storage.LoadSnapshotAsync(ct: ct);
        if (snapshot.CorruptMetadataPaths.Count > 0)
            throw new InvalidDataException("Novel metadata validation failed.");
        if (snapshot.Books.Count != manifest.Books.Count)
            throw new InvalidDataException("Migrated novel count does not match SQLite.");

        var actualById = snapshot.Books.ToDictionary(book => book.Id, StringComparer.Ordinal);
        foreach (var expected in manifest.Books)
        {
            if (!actualById.TryGetValue(expected.Id, out var actual))
                throw new InvalidDataException($"Migrated novel is missing: {expected.Id}");

            var actualManifest = ToManifestBook(actual);
            if (actualManifest != expected)
                throw new InvalidDataException($"Migrated novel validation failed: {expected.Id}");
        }
    }

    private async Task EnsureEmptyArrayFileAsync(string path, CancellationToken ct)
    {
        var result = await _json.ReadAsync<List<JsonElement>>(path, ct);
        if (result.Status == NovelJsonReadStatus.Missing)
        {
            await _json.WriteAsync(path, Array.Empty<object>(), ct);
            return;
        }

        if (result.Status != NovelJsonReadStatus.Success || result.Value is null)
            throw new InvalidDataException(result.Error ?? $"Invalid JSON array: {path}");
    }

    private async Task<bool> LegacyTableExistsAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'NovelBooks';",
            cancellationToken: ct)) > 0;
    }

    private async Task<List<LegacyNovelBookRow>> LoadLegacyBooksAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        const string sql = """
            SELECT Id, Title, Author, FilePath, CoverPath, ImportedAt, LastOpenedAt,
                   Language, UniqueIdentifier, ExtractedPath, ChapterCount,
                   CurrentChapterIndex, Progress, CurrentCharacterCount,
                   TotalCharacterCount, ManualSortOrder, ProfileId
            FROM NovelBooks;
            """;
        var rows = await connection.QueryAsync<LegacyNovelBookRow>(new CommandDefinition(
            sql,
            cancellationToken: ct));
        return rows.ToList();
    }

    private async Task DropLegacyTablesAsync(CancellationToken ct)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DROP TABLE IF EXISTS NovelReadingProgress;
                DROP TABLE IF EXISTS NovelReaderSettings;
                DROP TABLE IF EXISTS NovelBooks;
                """,
                transaction: transaction,
                cancellationToken: ct));
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private void CreateBackup()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:")
            throw new InvalidDataException("Novel migration requires a file-backed SQLite database.");

        var databasePath = Path.GetFullPath(builder.DataSource);
        var backupPath = databasePath + BackupSuffix;
        if (!File.Exists(backupPath))
            File.Copy(databasePath, backupPath, overwrite: false);
    }

    private string ResolveBookRoot(string bookId)
    {
        try
        {
            return _storage.ResolveRootPath(bookId);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidDataException($"Invalid legacy novel ID: {bookId}", ex);
        }
    }

    private static NovelStorageMigrationBook ToManifestBook(NovelBook book) =>
        new(
            book.Id,
            book.Folder,
            book.CurrentChapterIndex,
            book.CurrentCharacterCount,
            book.TotalCharacterCount,
            book.ProfileId);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static bool IsRecoverableMigrationFailure(Exception ex) =>
        ex is JsonException
            or IOException
            or UnauthorizedAccessException
            or SqliteException
            or InvalidDataException
            or InvalidOperationException;

    private sealed class LegacyNovelBookRow
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Author { get; set; }
        public string FilePath { get; set; } = string.Empty;
        public string? CoverPath { get; set; }
        public DateTime ImportedAt { get; set; }
        public DateTime? LastOpenedAt { get; set; }
        public string? Language { get; set; }
        public string? UniqueIdentifier { get; set; }
        public string? ExtractedPath { get; set; }
        public int ChapterCount { get; set; }
        public int CurrentChapterIndex { get; set; }
        public double Progress { get; set; }
        public int CurrentCharacterCount { get; set; }
        public int TotalCharacterCount { get; set; }
        public int ManualSortOrder { get; set; }
        public string? ProfileId { get; set; }
    }
}
