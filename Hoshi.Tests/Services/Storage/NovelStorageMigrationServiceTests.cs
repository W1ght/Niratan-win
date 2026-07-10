using FluentAssertions;
using Hoshi.Models.Novel;
using Hoshi.Services.Novels;
using Hoshi.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hoshi.Tests.Services.Storage;

public sealed class NovelStorageMigrationServiceTests
{
    [Fact]
    public async Task MigrateAsync_ExportsValidatesAndRetiresLegacyNovelTables()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var booksRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "Novels")).FullName;
        var bookRoot = Directory.CreateDirectory(Path.Combine(booksRoot, "book-a")).FullName;
        var sourceEpub = Path.Combine(temp.Path, "source.epub");
        await File.WriteAllTextAsync(sourceEpub, "epub", ct);
        var coverPath = Path.Combine(bookRoot, "cover.jpg");
        await File.WriteAllTextAsync(coverPath, "cover", ct);
        var dbPath = Path.Combine(temp.Path, "hoshi.db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        var lastOpened = new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
        await CreateLegacyDatabaseAsync(
            connectionString,
            sourceEpub,
            coverPath,
            bookRoot,
            lastOpened,
            ct);

        var json = new NiratanJsonFileStore();
        var sidecars = new NovelBookSidecarService(json);
        var newerBookmark = new NovelBookmark(
            5,
            0.75,
            4321,
            new DateTimeOffset(lastOpened).AddDays(1));
        await sidecars.SaveBookmarkAsync(bookRoot, newerBookmark, ct);
        var storage = new NovelBookStorageService(booksRoot, json, sidecars);
        var service = new NovelStorageMigrationService(
            connectionString,
            booksRoot,
            storage,
            sidecars,
            json,
            NullLogger<NovelStorageMigrationService>.Instance);

        var result = await service.MigrateAsync(ct);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.IsReadOnly.Should().BeFalse();
        result.MigratedBookCount.Should().Be(1);
        File.Exists(dbPath + ".pre-novel-files-v1.bak").Should().BeTrue();
        File.Exists(Path.Combine(bookRoot, "metadata.json")).Should().BeTrue();
        File.Exists(Path.Combine(bookRoot, "book-a.epub")).Should().BeTrue();
        File.Exists(Path.Combine(booksRoot, "book_order.json")).Should().BeTrue();
        File.Exists(Path.Combine(booksRoot, "shelves.json")).Should().BeTrue();
        File.Exists(Path.Combine(booksRoot, "novel_storage_migration_v1.json")).Should().BeTrue();
        (await sidecars.LoadBookmarkAsync(bookRoot, ct)).Should().BeEquivalentTo(newerBookmark);

        var metadata = await json.ReadAsync<NovelBookMetadata>(
            Path.Combine(bookRoot, "metadata.json"),
            ct);
        metadata.Value.Should().BeEquivalentTo(new NovelBookMetadata(
            "book-a",
            "原題",
            "book-a.epub",
            "cover.jpg",
            "book-a",
            new DateTimeOffset(lastOpened),
            null,
            "default-ja",
            "ja"));
        (await storage.LoadBookOrderAsync(ct)).Should().Equal("book-a");
        (await File.ReadAllTextAsync(Path.Combine(booksRoot, "shelves.json"), ct)).Trim()
            .Should().Be("[]");
        (await TableExistsAsync(connectionString, "NovelBooks", ct)).Should().BeFalse();
        (await TableExistsAsync(connectionString, "NovelReadingProgress", ct)).Should().BeFalse();
        (await TableExistsAsync(connectionString, "NovelReaderSettings", ct)).Should().BeFalse();

        var retry = await service.MigrateAsync(ct);
        retry.IsSuccess.Should().BeTrue(retry.ErrorMessage);
        retry.IsReadOnly.Should().BeFalse();

        File.Delete(Path.Combine(booksRoot, "novel_storage_migration_v1.json"));
        var interruptedRetry = await service.MigrateAsync(ct);
        interruptedRetry.IsSuccess.Should().BeTrue(interruptedRetry.ErrorMessage);
        File.Exists(Path.Combine(booksRoot, "novel_storage_migration_v1.json"))
            .Should().BeTrue();
    }

    [Fact]
    public async Task MigrateAsync_InvalidExistingMetadataFailsClosedAndPreservesLegacyTable()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var booksRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "Novels")).FullName;
        var bookRoot = Directory.CreateDirectory(Path.Combine(booksRoot, "book-a")).FullName;
        var sourceEpub = Path.Combine(temp.Path, "source.epub");
        await File.WriteAllTextAsync(sourceEpub, "epub", ct);
        var dbPath = Path.Combine(temp.Path, "hoshi.db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        await CreateLegacyDatabaseAsync(
            connectionString,
            sourceEpub,
            null,
            bookRoot,
            DateTime.SpecifyKind(DateTime.UnixEpoch, DateTimeKind.Utc),
            ct);
        var metadataPath = Path.Combine(bookRoot, "metadata.json");
        await File.WriteAllTextAsync(metadataPath, "{broken", ct);
        var json = new NiratanJsonFileStore();
        var sidecars = new NovelBookSidecarService(json);
        var storage = new NovelBookStorageService(booksRoot, json, sidecars);
        var service = new NovelStorageMigrationService(
            connectionString,
            booksRoot,
            storage,
            sidecars,
            json,
            NullLogger<NovelStorageMigrationService>.Instance);

        var result = await service.MigrateAsync(ct);

        result.IsSuccess.Should().BeFalse();
        result.IsReadOnly.Should().BeTrue();
        result.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        File.Exists(dbPath + ".pre-novel-files-v1.bak").Should().BeTrue();
        (await TableExistsAsync(connectionString, "NovelBooks", ct)).Should().BeTrue();
        (await File.ReadAllTextAsync(metadataPath, ct)).Should().Be("{broken");
        File.Exists(Path.Combine(booksRoot, "novel_storage_migration_v1.json"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task MigrateAsync_FreshVideoOnlyDatabaseCreatesEmptyNovelGlobals()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var booksRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "Novels")).FullName;
        var dbPath = Path.Combine(temp.Path, "hoshi.db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
            await connection.OpenAsync(ct);
        var service = CreateService(connectionString, booksRoot);

        var result = await service.MigrateAsync(ct);

        result.IsSuccess.Should().BeTrue(result.ErrorMessage);
        result.MigratedBookCount.Should().Be(0);
        (await File.ReadAllTextAsync(Path.Combine(booksRoot, "book_order.json"), ct)).Trim()
            .Should().Be("[]");
        (await File.ReadAllTextAsync(Path.Combine(booksRoot, "shelves.json"), ct)).Trim()
            .Should().Be("[]");
        File.Exists(Path.Combine(booksRoot, "novel_storage_migration_v1.json"))
            .Should().BeTrue();
        (await TableExistsAsync(connectionString, "NovelBooks", ct)).Should().BeFalse();
    }

    [Fact]
    public async Task MigrateAsync_InvalidGlobalJsonFailsClosedWithoutOverwritingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var booksRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "Novels")).FullName;
        var dbPath = Path.Combine(temp.Path, "hoshi.db");
        var connectionString = $"Data Source={dbPath};Pooling=False";
        await using (var connection = new SqliteConnection(connectionString))
            await connection.OpenAsync(ct);
        var shelvesPath = Path.Combine(booksRoot, "shelves.json");
        await File.WriteAllTextAsync(shelvesPath, "{broken", ct);
        var service = CreateService(connectionString, booksRoot);

        var result = await service.MigrateAsync(ct);

        result.IsSuccess.Should().BeFalse();
        result.IsReadOnly.Should().BeTrue();
        (await File.ReadAllTextAsync(shelvesPath, ct)).Should().Be("{broken");
    }

    private static NovelStorageMigrationService CreateService(
        string connectionString,
        string booksRoot)
    {
        var json = new NiratanJsonFileStore();
        var sidecars = new NovelBookSidecarService(json);
        var storage = new NovelBookStorageService(booksRoot, json, sidecars);
        return new NovelStorageMigrationService(
            connectionString,
            booksRoot,
            storage,
            sidecars,
            json,
            NullLogger<NovelStorageMigrationService>.Instance);
    }

    private static async Task CreateLegacyDatabaseAsync(
        string connectionString,
        string filePath,
        string? coverPath,
        string extractedPath,
        DateTime lastOpenedAt,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE NovelBooks (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Author TEXT,
                FilePath TEXT NOT NULL UNIQUE,
                CoverPath TEXT,
                ImportedAt TEXT NOT NULL,
                LastOpenedAt TEXT,
                Language TEXT,
                UniqueIdentifier TEXT,
                ExtractedPath TEXT,
                ChapterCount INTEGER NOT NULL DEFAULT 0,
                CurrentChapterIndex INTEGER NOT NULL DEFAULT 0,
                Progress REAL NOT NULL DEFAULT 0.0,
                CurrentCharacterCount INTEGER NOT NULL DEFAULT 0,
                TotalCharacterCount INTEGER NOT NULL DEFAULT 0,
                ManualSortOrder INTEGER NOT NULL DEFAULT 0,
                ProfileId TEXT
            );
            CREATE TABLE NovelReadingProgress (
                BookId TEXT PRIMARY KEY,
                LocationJson TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            CREATE TABLE NovelReaderSettings (
                Scope TEXT NOT NULL,
                ScopeId TEXT NOT NULL,
                SettingsJson TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (Scope, ScopeId)
            );
            INSERT INTO NovelBooks (
                Id, Title, FilePath, CoverPath, ImportedAt, LastOpenedAt, Language,
                ExtractedPath, ChapterCount, CurrentChapterIndex, Progress,
                CurrentCharacterCount, TotalCharacterCount, ManualSortOrder, ProfileId)
            VALUES (
                $id, $title, $filePath, $coverPath, $importedAt, $lastOpenedAt, $language,
                $extractedPath, 10, 2, 0.25, 1234, 9000, 3, $profileId);
            """;
        command.Parameters.AddWithValue("$id", "book-a");
        command.Parameters.AddWithValue("$title", "原題");
        command.Parameters.AddWithValue("$filePath", filePath);
        command.Parameters.AddWithValue("$coverPath", (object?)coverPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$importedAt", DateTime.UnixEpoch.ToString("O"));
        command.Parameters.AddWithValue("$lastOpenedAt", lastOpenedAt.ToString("O"));
        command.Parameters.AddWithValue("$language", "ja");
        command.Parameters.AddWithValue("$extractedPath", extractedPath);
        command.Parameters.AddWithValue("$profileId", "default-ja");
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<bool> TableExistsAsync(
        string connectionString,
        string tableName,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $name;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return (long)(await command.ExecuteScalarAsync(ct))! > 0;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
