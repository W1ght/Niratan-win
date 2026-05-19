# Novel EPUB Module Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an isolated Novel module that can import local EPUB files into SQLite, list them in a new Novel library page, and open a separate Novel reader placeholder without changing existing comic behavior.

**Architecture:** Build a narrow vertical slice parallel to the current comic flow. Use `Novel` for domain names and `Epub` only for format-specific import code. Keep storage in the existing migration/DataService pipeline for this first slice, then add `INovelLibraryService` as the domain boundary consumed by `NovelLibraryViewModel`.

**Tech Stack:** WinUI 3, C#/.NET 10, CommunityToolkit.Mvvm, Dapper, Microsoft.Data.Sqlite, Microsoft.Extensions.DependencyInjection, xUnit v3, FluentAssertions.

---

## File Structure

Create:

- `Hoshi/Models/NovelBook.cs`: domain model for imported EPUB novels.
- `Hoshi/Models/Data/NovelReadingProgress.cs`: persisted reading location model for future reader work.
- `Hoshi/Models/DTO/NovelImportResult.cs`: import result returned by EPUB import code.
- `Hoshi/Models/DTO/NovelReaderNavigationArgs.cs`: route parameter for the novel reader placeholder.
- `Hoshi/Services/Novels/INovelLibraryService.cs`: domain-facing service interface for library list/import.
- `Hoshi/Services/Novels/NovelLibraryService.cs`: result-wrapped orchestration between import, storage, and logging.
- `Hoshi/Services/Novels/INovelEpubImportService.cs`: EPUB-specific metadata import interface.
- `Hoshi/Services/Novels/NovelEpubImportService.cs`: minimal EPUB validation and metadata extraction.
- `Hoshi/Services/Storage/Migrations/Migration_003.cs`: novel tables.
- `Hoshi/ViewModels/Components/NovelBookItemViewModel.cs`: display wrapper for library items.
- `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`: import/list/open commands for the Novel library.
- `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`: placeholder reader state.
- `Hoshi/Views/Pages/NovelLibraryPage.xaml`: Novel library UI.
- `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`: library page code-behind.
- `Hoshi/Views/Pages/NovelReaderPage.xaml`: placeholder reader UI.
- `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`: reader page code-behind.
- `Hoshi.Tests/Services/Novels/NovelEpubImportServiceTests.cs`: import validation and fallback tests.
- `Hoshi.Tests/Services/Storage/NovelDataServiceTests.cs`: storage and migration tests.
- `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`: ViewModel behavior tests.

Modify:

- `Hoshi/App.xaml.cs`: register Novel services/ViewModels.
- `Hoshi/Enums/AppMode.cs`: add `NovelReader`.
- `Hoshi/Enums/AppPage.cs`: add `NovelLibraryPage`.
- `Hoshi/Services/Storage/DatabaseMigrator.cs`: include `Migration_003`.
- `Hoshi/Services/Storage/IDataService.cs`: add Novel storage methods.
- `Hoshi/Services/Storage/DataService.cs`: implement Novel storage methods.
- `Hoshi/Services/NavigationService.cs`: recognize `NovelLibraryPage`.
- `Hoshi/Views/Pages/NavigationPage.xaml`: add Novel navigation item.
- `Hoshi/Views/Pages/ShellPage.xaml.cs`: route `AppMode.NovelReader` to `NovelReaderPage`.

Do not modify:

- Existing comic models and comic service behavior except where shared navigation must recognize new pages.
- Existing comic reader body or image rendering.
- `agents.md`, unless the user explicitly asks.

---

### Task 1: Add Novel Models

**Files:**
- Create: `Hoshi/Models/NovelBook.cs`
- Create: `Hoshi/Models/Data/NovelReadingProgress.cs`
- Create: `Hoshi/Models/DTO/NovelImportResult.cs`
- Create: `Hoshi/Models/DTO/NovelReaderNavigationArgs.cs`

- [ ] **Step 1: Add `NovelBook`**

Create `Hoshi/Models/NovelBook.cs`:

```csharp
using System;

namespace Hoshi.Models;

public class NovelBook
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string? Author { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastOpenedAt { get; set; }
    public string? Language { get; set; }
    public string? UniqueIdentifier { get; set; }
}
```

- [ ] **Step 2: Add `NovelReadingProgress`**

Create `Hoshi/Models/Data/NovelReadingProgress.cs`:

```csharp
using System;

namespace Hoshi.Models.Data;

public class NovelReadingProgress
{
    public string LocationJson { get; set; } = "{}";
    public double? Progression { get; set; }
    public string? ChapterHref { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
```

- [ ] **Step 3: Add DTOs**

Create `Hoshi/Models/DTO/NovelImportResult.cs`:

```csharp
using Hoshi.Models;

namespace Hoshi.Models.DTO;

public sealed record NovelImportResult(NovelBook Book);
```

Create `Hoshi/Models/DTO/NovelReaderNavigationArgs.cs`:

```csharp
namespace Hoshi.Models.DTO;

public sealed record NovelReaderNavigationArgs(string BookId);
```

- [ ] **Step 4: Build model-only changes**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\Hoshi.slnx -c Debug -p:Platform=x64 --no-restore
```

Expected: build succeeds. Existing nullable warnings may remain.

- [ ] **Step 5: Commit**

```powershell
git add Hoshi\Models\NovelBook.cs Hoshi\Models\Data\NovelReadingProgress.cs Hoshi\Models\DTO\NovelImportResult.cs Hoshi\Models\DTO\NovelReaderNavigationArgs.cs
git commit -m "feat(novels): add novel domain models"
```

---

### Task 2: Add Novel Database Migration and Storage Methods

**Files:**
- Create: `Hoshi/Services/Storage/Migrations/Migration_003.cs`
- Modify: `Hoshi/Services/Storage/DatabaseMigrator.cs`
- Modify: `Hoshi/Services/Storage/IDataService.cs`
- Modify: `Hoshi/Services/Storage/DataService.cs`
- Test: `Hoshi.Tests/Services/Storage/NovelDataServiceTests.cs`

- [ ] **Step 1: Write migration/storage tests**

Create `Hoshi.Tests/Services/Storage/NovelDataServiceTests.cs`:

```csharp
using System.Reflection;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Hoshi.Tests.Services.Storage;

public class NovelDataServiceTests
{
    [Fact]
    public async Task Migration003_CreatesNovelTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await InvokeMigration003Async(connection, transaction);
        await transaction.CommitAsync();

        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('NovelBooks', 'NovelReadingProgress', 'NovelReaderSettings');
            """;

        var count = (long)(await command.ExecuteScalarAsync())!;
        count.Should().Be(3);
    }

    [Fact]
    public async Task Migration003_PreventsDuplicateFilePaths()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InvokeMigration003Async(connection, transaction);
        await transaction.CommitAsync();

        var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO NovelBooks
                (Id, Title, FilePath, ImportedAt)
            VALUES
                ('one', 'One', 'D:\Books\a.epub', '2026-05-19T00:00:00Z'),
                ('two', 'Two', 'D:\Books\a.epub', '2026-05-19T00:00:00Z');
            """;

        var act = async () => await insert.ExecuteNonQueryAsync();
        await act.Should().ThrowAsync<SqliteException>();
    }

    private static async Task InvokeMigration003Async(
        SqliteConnection connection,
        SqliteTransaction transaction
    )
    {
        var appAssembly = typeof(Hoshi.Models.NovelBook).Assembly;
        var migrationType = appAssembly.GetType(
            "Hoshi.Services.Storage.Migrations.Migration_003",
            throwOnError: true
        )!;
        var migration = Activator.CreateInstance(migrationType, nonPublic: true)!;
        var method = migrationType.GetMethod(
            "UpAsync",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        )!;

        await (Task)method.Invoke(migration, [connection, transaction])!;
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --no-build --filter NovelDataServiceTests
```

Expected: fail because `NovelBook` and `Migration_003` do not exist.

- [ ] **Step 3: Add `Migration_003`**

Create `Hoshi/Services/Storage/Migrations/Migration_003.cs`:

```csharp
using System.Data.Common;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Hoshi.Services.Storage.Migrations;

internal class Migration_003 : IMigration
{
    public int Version => 3;
    public string Description => "Add novel EPUB library tables";

    public async Task UpAsync(SqliteConnection connection, DbTransaction transaction)
    {
        await connection.ExecuteAsync(
            """
            CREATE TABLE IF NOT EXISTS NovelBooks (
                Id TEXT PRIMARY KEY,
                Title TEXT NOT NULL,
                Author TEXT,
                FilePath TEXT NOT NULL UNIQUE,
                CoverPath TEXT,
                ImportedAt TEXT NOT NULL,
                LastOpenedAt TEXT,
                Language TEXT,
                UniqueIdentifier TEXT
            );

            CREATE TABLE IF NOT EXISTS NovelReadingProgress (
                BookId TEXT PRIMARY KEY,
                LocationJson TEXT NOT NULL,
                Progression REAL,
                ChapterHref TEXT,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY (BookId) REFERENCES NovelBooks(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS NovelReaderSettings (
                Scope TEXT NOT NULL,
                ScopeId TEXT NOT NULL,
                SettingsJson TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY (Scope, ScopeId)
            );

            CREATE INDEX IF NOT EXISTS IX_NovelBooks_Title
                ON NovelBooks (Title);
            """,
            transaction: transaction
        );
    }
}
```

- [ ] **Step 4: Register migration**

Modify `Hoshi/Services/Storage/DatabaseMigrator.cs`:

```csharp
private static readonly IReadOnlyList<IMigration> AllMigrations =
[
    new Migration_001(),
    new Migration_002(),
    new Migration_003(),
];
```

- [ ] **Step 5: Add storage interface methods**

Append to `Hoshi/Services/Storage/IDataService.cs` near the other read/write methods:

```csharp
Task<IReadOnlyList<NovelBook>> GetNovelBooksAsync(
    string? queryText = null,
    CancellationToken ct = default
);
Task<NovelBook?> GetNovelBookAsync(string bookId, CancellationToken ct = default);
Task UpsertNovelBookAsync(NovelBook book, CancellationToken ct = default);
Task UpdateNovelLastOpenedAsync(string bookId, DateTime lastOpenedAt, CancellationToken ct = default);
```

Add `using System;` if not already present.

- [ ] **Step 6: Implement storage methods**

Append to `Hoshi/Services/Storage/DataService.cs` before `CleanupUnfavoriteComicsDataAsync`:

```csharp
public async Task<IReadOnlyList<NovelBook>> GetNovelBooksAsync(
    string? queryText = null,
    CancellationToken ct = default
)
{
    using var connection = await GetOpenConnectionAsync();
    const string sql = """
        SELECT Id, Title, Author, FilePath, CoverPath, ImportedAt, LastOpenedAt, Language, UniqueIdentifier
        FROM NovelBooks
        WHERE @QueryText IS NULL
            OR TRIM(@QueryText) = ''
            OR Title LIKE '%' || @QueryText || '%' COLLATE NOCASE
            OR Author LIKE '%' || @QueryText || '%' COLLATE NOCASE
        ORDER BY COALESCE(LastOpenedAt, ImportedAt) DESC, Title ASC;
        """;

    var result = await connection.QueryAsync<NovelBook>(
        new CommandDefinition(sql, new { QueryText = queryText?.Trim() }, cancellationToken: ct)
    );
    return result.ToList();
}

public async Task<NovelBook?> GetNovelBookAsync(string bookId, CancellationToken ct = default)
{
    using var connection = await GetOpenConnectionAsync();
    const string sql = """
        SELECT Id, Title, Author, FilePath, CoverPath, ImportedAt, LastOpenedAt, Language, UniqueIdentifier
        FROM NovelBooks
        WHERE Id = @BookId;
        """;

    return await connection.QueryFirstOrDefaultAsync<NovelBook>(
        new CommandDefinition(sql, new { BookId = bookId }, cancellationToken: ct)
    );
}

public async Task UpsertNovelBookAsync(NovelBook book, CancellationToken ct = default)
{
    using var connection = await GetOpenConnectionAsync();
    const string sql = """
        INSERT INTO NovelBooks
            (Id, Title, Author, FilePath, CoverPath, ImportedAt, LastOpenedAt, Language, UniqueIdentifier)
        VALUES
            (@Id, @Title, @Author, @FilePath, @CoverPath, @ImportedAt, @LastOpenedAt, @Language, @UniqueIdentifier)
        ON CONFLICT(FilePath) DO UPDATE SET
            Title = excluded.Title,
            Author = excluded.Author,
            CoverPath = excluded.CoverPath,
            Language = excluded.Language,
            UniqueIdentifier = excluded.UniqueIdentifier;
        """;

    await connection.ExecuteAsync(new CommandDefinition(sql, book, cancellationToken: ct));
}

public async Task UpdateNovelLastOpenedAsync(
    string bookId,
    DateTime lastOpenedAt,
    CancellationToken ct = default
)
{
    using var connection = await GetOpenConnectionAsync();
    await connection.ExecuteAsync(
        new CommandDefinition(
            "UPDATE NovelBooks SET LastOpenedAt = @LastOpenedAt WHERE Id = @BookId;",
            new { BookId = bookId, LastOpenedAt = lastOpenedAt },
            cancellationToken: ct
        )
    );
}
```

- [ ] **Step 7: Run storage tests**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --filter NovelDataServiceTests
```

Expected: `NovelDataServiceTests` pass.

- [ ] **Step 8: Commit**

```powershell
git add Hoshi\Services\Storage\Migrations\Migration_003.cs Hoshi\Services\Storage\DatabaseMigrator.cs Hoshi\Services\Storage\IDataService.cs Hoshi\Services\Storage\DataService.cs Hoshi.Tests\Services\Storage\NovelDataServiceTests.cs
git commit -m "feat(novels): add novel storage schema"
```

---

### Task 3: Add EPUB Import Service

**Files:**
- Create: `Hoshi/Services/Novels/INovelEpubImportService.cs`
- Create: `Hoshi/Services/Novels/NovelEpubImportService.cs`
- Test: `Hoshi.Tests/Services/Novels/NovelEpubImportServiceTests.cs`

- [ ] **Step 1: Write EPUB import tests**

Create `Hoshi.Tests/Services/Novels/NovelEpubImportServiceTests.cs`:

```csharp
using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Hoshi.Services.Novels;

namespace Hoshi.Tests.Services.Novels;

public class NovelEpubImportServiceTests
{
    [Fact]
    public async Task ImportAsync_ReturnsFailure_WhenFileIsNotEpub()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "not epub");
        var sut = new NovelEpubImportService(NullLogger<NovelEpubImportService>.Instance);

        var result = await sut.ImportAsync(path);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain(".epub");
    }

    [Fact]
    public async Task ImportAsync_UsesFileNameFallback_WhenMetadataIsMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.epub");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var mimetype = archive.CreateEntry("mimetype");
            await using var mimeStream = mimetype.Open();
            await using var writer = new StreamWriter(mimeStream);
            await writer.WriteAsync("application/epub+zip");
        }

        var sut = new NovelEpubImportService(NullLogger<NovelEpubImportService>.Instance);

        var result = await sut.ImportAsync(path);

        result.IsSuccess.Should().BeTrue();
        result.Value!.Book.Title.Should().Be(Path.GetFileNameWithoutExtension(path));
        result.Value.Book.FilePath.Should().Be(Path.GetFullPath(path));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --no-build --filter NovelEpubImportServiceTests
```

Expected: fail because service files do not exist.

- [ ] **Step 3: Add EPUB import interface**

Create `Hoshi/Services/Novels/INovelEpubImportService.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;

namespace Hoshi.Services.Novels;

public interface INovelEpubImportService
{
    Task<Result<NovelImportResult>> ImportAsync(string filePath, CancellationToken ct = default);
}
```

- [ ] **Step 4: Add minimal EPUB import implementation**

Create `Hoshi/Services/Novels/NovelEpubImportService.cs`:

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;

namespace Hoshi.Services.Novels;

internal sealed class NovelEpubImportService : INovelEpubImportService
{
    private readonly ILogger<NovelEpubImportService> _logger;

    public NovelEpubImportService(ILogger<NovelEpubImportService> logger)
    {
        _logger = logger;
    }

    public async Task<Result<NovelImportResult>> ImportAsync(
        string filePath,
        CancellationToken ct = default
    )
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return Result<NovelImportResult>.Failure("The selected EPUB file does not exist.");

            if (!string.Equals(Path.GetExtension(filePath), ".epub", StringComparison.OrdinalIgnoreCase))
                return Result<NovelImportResult>.Failure("Please select a .epub file.");

            string fullPath = Path.GetFullPath(filePath);
            var book = await Task.Run(() => ReadMetadata(fullPath), ct);
            return Result<NovelImportResult>.Success(new NovelImportResult(book));
        }
        catch (OperationCanceledException)
        {
            return Result<NovelImportResult>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import EPUB {FilePath}", filePath);
            return Result<NovelImportResult>.Failure(ex.Message, "EPUB import failed");
        }
    }

    private static NovelBook ReadMetadata(string fullPath)
    {
        using var archive = ZipFile.OpenRead(fullPath);
        var opfEntry = archive.Entries.FirstOrDefault(e =>
            e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase)
        );

        if (opfEntry == null)
            return CreateFallbackBook(fullPath);

        using var stream = opfEntry.Open();
        var doc = XDocument.Load(stream);
        XNamespace dc = "http://purl.org/dc/elements/1.1/";

        string title = doc.Descendants(dc + "title").FirstOrDefault()?.Value?.Trim()
            ?? Path.GetFileNameWithoutExtension(fullPath);
        string? author = doc.Descendants(dc + "creator").FirstOrDefault()?.Value?.Trim();
        string? language = doc.Descendants(dc + "language").FirstOrDefault()?.Value?.Trim();
        string? identifier = doc.Descendants(dc + "identifier").FirstOrDefault()?.Value?.Trim();

        return new NovelBook
        {
            Title = string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(fullPath) : title,
            Author = string.IsNullOrWhiteSpace(author) ? null : author,
            FilePath = fullPath,
            ImportedAt = DateTime.UtcNow,
            Language = string.IsNullOrWhiteSpace(language) ? null : language,
            UniqueIdentifier = string.IsNullOrWhiteSpace(identifier) ? null : identifier,
        };
    }

    private static NovelBook CreateFallbackBook(string fullPath) =>
        new()
        {
            Title = Path.GetFileNameWithoutExtension(fullPath),
            FilePath = fullPath,
            ImportedAt = DateTime.UtcNow,
        };
}
```

- [ ] **Step 5: Run EPUB import tests**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --filter NovelEpubImportServiceTests
```

Expected: tests pass.

- [ ] **Step 6: Commit**

```powershell
git add Hoshi\Services\Novels\INovelEpubImportService.cs Hoshi\Services\Novels\NovelEpubImportService.cs Hoshi.Tests\Services\Novels\NovelEpubImportServiceTests.cs
git commit -m "feat(novels): add epub import service"
```

---

### Task 4: Add Novel Library Service

**Files:**
- Create: `Hoshi/Services/Novels/INovelLibraryService.cs`
- Create: `Hoshi/Services/Novels/NovelLibraryService.cs`
- Modify: `Hoshi/App.xaml.cs`

- [ ] **Step 1: Add library service interface**

Create `Hoshi/Services/Novels/INovelLibraryService.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Common;

namespace Hoshi.Services.Novels;

public interface INovelLibraryService
{
    Task<Result<IReadOnlyList<NovelBook>>> GetNovelBooksAsync(
        string? queryText = null,
        CancellationToken ct = default
    );

    Task<Result<NovelBook>> ImportEpubAsync(string filePath, CancellationToken ct = default);

    Task<Result<NovelBook?>> GetNovelBookAsync(string bookId, CancellationToken ct = default);

    Task<Result> MarkOpenedAsync(string bookId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Add library service implementation**

Create `Hoshi/Services/Novels/NovelLibraryService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Services.Storage;

namespace Hoshi.Services.Novels;

internal sealed class NovelLibraryService : INovelLibraryService
{
    private readonly IDataService _dataService;
    private readonly INovelEpubImportService _epubImportService;
    private readonly ILogger<NovelLibraryService> _logger;

    public NovelLibraryService(
        IDataService dataService,
        INovelEpubImportService epubImportService,
        ILogger<NovelLibraryService> logger
    )
    {
        _dataService = dataService;
        _epubImportService = epubImportService;
        _logger = logger;
    }

    public async Task<Result<IReadOnlyList<NovelBook>>> GetNovelBooksAsync(
        string? queryText = null,
        CancellationToken ct = default
    ) =>
        await ExecuteAsync(
            async token => Result<IReadOnlyList<NovelBook>>.Success(
                await _dataService.GetNovelBooksAsync(queryText, token)
            ),
            "Error loading novels",
            ct
        );

    public async Task<Result<NovelBook>> ImportEpubAsync(
        string filePath,
        CancellationToken ct = default
    )
    {
        var importResult = await _epubImportService.ImportAsync(filePath, ct);
        if (!importResult.IsSuccess)
            return Result<NovelBook>.Failure(importResult.Error!, importResult.ErrorTitle ?? "Import failed");

        return await ExecuteAsync(
            async token =>
            {
                var book = importResult.Value!.Book;
                await _dataService.UpsertNovelBookAsync(book, token);
                _logger.LogInformation("Imported novel EPUB {FilePath}", book.FilePath);
                return Result<NovelBook>.Success(book);
            },
            "Error saving novel",
            ct
        );
    }

    public async Task<Result<NovelBook?>> GetNovelBookAsync(
        string bookId,
        CancellationToken ct = default
    ) =>
        await ExecuteAsync(
            async token => Result<NovelBook?>.Success(await _dataService.GetNovelBookAsync(bookId, token)),
            "Error loading novel",
            ct
        );

    public async Task<Result> MarkOpenedAsync(string bookId, CancellationToken ct = default)
    {
        try
        {
            await _dataService.UpdateNovelLastOpenedAsync(bookId, DateTime.UtcNow, ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating novel last opened time for {BookId}", bookId);
            return Result.Failure(ex.Message, "Error opening novel");
        }
    }

    private async Task<Result<T>> ExecuteAsync<T>(
        Func<CancellationToken, Task<Result<T>>> action,
        string errorTitle,
        CancellationToken ct
    )
    {
        try
        {
            return await action(ct);
        }
        catch (OperationCanceledException)
        {
            return Result<T>.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ErrorTitle}", errorTitle);
            return Result<T>.Failure(ex.Message, errorTitle);
        }
    }
}
```

- [ ] **Step 3: Register services**

Modify `Hoshi/App.xaml.cs`:

Add using:

```csharp
using Hoshi.Services.Novels;
```

Add registrations:

```csharp
services.AddSingleton<INovelEpubImportService, NovelEpubImportService>();
services.AddSingleton<INovelLibraryService, NovelLibraryService>();
```

- [ ] **Step 4: Build**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\Hoshi.slnx -c Debug -p:Platform=x64 --no-restore
```

Expected: build succeeds.

- [ ] **Step 5: Commit**

```powershell
git add Hoshi\Services\Novels\INovelLibraryService.cs Hoshi\Services\Novels\NovelLibraryService.cs Hoshi\App.xaml.cs
git commit -m "feat(novels): add novel library service"
```

---

### Task 5: Add Novel Library ViewModel

**Files:**
- Create: `Hoshi/ViewModels/Components/NovelBookItemViewModel.cs`
- Create: `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`
- Modify: `Hoshi/App.xaml.cs`
- Test: `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs`

- [ ] **Step 1: Add ViewModel tests**

Create `Hoshi.Tests/ViewModels/Pages/NovelLibraryPageViewModelTests.cs` with a small fake service:

```csharp
using FluentAssertions;
using Moq;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Services.Novels;
using Hoshi.Services.UI;
using Hoshi.Tests.TestUtils;
using Hoshi.ViewModels.Components;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Tests.ViewModels.Pages;

public class NovelLibraryPageViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsNovelBooks()
    {
        var service = new Mock<INovelLibraryService>();
        service.Setup(s => s.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NovelBook>>.Success(
                [new NovelBook { Id = "book-1", Title = "Book One", FilePath = "D:\\Books\\one.epub" }]
            ));

        var sut = CreateSut(service.Object);

        await sut.InitializeAsync();

        sut.NovelBooks.Should().ContainSingle();
        sut.NovelBooks[0].Book.Title.Should().Be("Book One");
    }

    [Fact]
    public async Task ImportCommand_ShowsError_WhenPickerCancelled()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.OpenFilePickerAsync(".epub")).ReturnsAsync((string?)null);
        var notification = new Mock<INotificationService>();

        var sut = CreateSut(dialogService: dialog.Object, notificationService: notification.Object);

        await sut.ImportNovelCommand.ExecuteAsync(null);

        notification.Verify(n => n.ShowError(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void OpenNovelCommand_SendsNavigateMessage()
    {
        var messenger = new FakeMessenger();
        var sut = CreateSut(messenger: messenger);
        var book = new NovelBook { Id = "book-1", Title = "Book One", FilePath = "D:\\Books\\one.epub" };

        sut.OpenNovelCommand.Execute(new NovelBookItemViewModel(book));

        messenger.SentMessages.Should().Contain(m => m is SwitchAppModeMessage);
    }

    private static NovelLibraryPageViewModel CreateSut(
        INovelLibraryService? novelService = null,
        IDialogService? dialogService = null,
        INotificationService? notificationService = null,
        FakeMessenger? messenger = null
    )
    {
        var serviceMock = new Mock<INovelLibraryService>();
        serviceMock.Setup(s => s.GetNovelBooksAsync(null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<IReadOnlyList<NovelBook>>.Success([]));

        return new NovelLibraryPageViewModel(
            novelService ?? serviceMock.Object,
            dialogService ?? Mock.Of<IDialogService>(),
            notificationService ?? Mock.Of<INotificationService>(),
            messenger ?? new FakeMessenger()
        );
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --no-build --filter NovelLibraryPageViewModelTests
```

Expected: fail because ViewModels do not exist.

- [ ] **Step 3: Add item ViewModel**

Create `Hoshi/ViewModels/Components/NovelBookItemViewModel.cs`:

```csharp
using Hoshi.Models;

namespace Hoshi.ViewModels.Components;

public class NovelBookItemViewModel
{
    public NovelBook Book { get; }

    public NovelBookItemViewModel(NovelBook book)
    {
        Book = book;
    }
}
```

- [ ] **Step 4: Add page ViewModel**

Create `Hoshi/ViewModels/Pages/NovelLibraryPageViewModel.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Messages;
using Hoshi.Models.DTO;
using Hoshi.Services.Novels;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Components;

namespace Hoshi.ViewModels.Pages;

public partial class NovelLibraryPageViewModel : ObservableObject
{
    private readonly INovelLibraryService _novelLibraryService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notificationService;
    private readonly IMessenger _messenger;
    private CancellationTokenSource _cts = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoNovels))]
    public partial List<NovelBookItemViewModel> NovelBooks { get; set; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoNovels))]
    public partial bool IsContentLoading { get; set; }

    public bool NoNovels => !IsContentLoading && NovelBooks.Count == 0;

    public NovelLibraryPageViewModel(
        INovelLibraryService novelLibraryService,
        IDialogService dialogService,
        INotificationService notificationService,
        IMessenger messenger
    )
    {
        _novelLibraryService = novelLibraryService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _messenger = messenger;
    }

    public async Task InitializeAsync() => await LoadNovelsAsync();

    public void OnNavigatedFrom()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    [RelayCommand]
    private async Task ImportNovelAsync()
    {
        var filePath = await _dialogService.OpenFilePickerAsync(".epub");
        if (filePath == null)
            return;

        var result = await _novelLibraryService.ImportEpubAsync(filePath);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        _notificationService.ShowSuccess("EPUB imported.", "Novel imported");
        await LoadNovelsAsync();
    }

    [RelayCommand]
    private void OpenNovel(NovelBookItemViewModel item)
    {
        _messenger.Send(
            new SwitchAppModeMessage(
                AppMode.NovelReader,
                new NovelReaderNavigationArgs(item.Book.Id)
            )
        );
    }

    private async Task LoadNovelsAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        IsContentLoading = true;
        var result = await _novelLibraryService.GetNovelBooksAsync(ct: _cts.Token);

        if (result.IsSuccess)
            NovelBooks = result.Value!.Select(book => new NovelBookItemViewModel(book)).ToList();
        else if (!result.IsCancelled)
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);

        IsContentLoading = false;
    }
}
```

- [ ] **Step 5: Register ViewModel**

Modify `Hoshi/App.xaml.cs`:

```csharp
services.AddTransient<NovelLibraryPageViewModel>();
```

- [ ] **Step 6: Run ViewModel tests**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --filter NovelLibraryPageViewModelTests
```

Expected: tests pass. `FakeMessenger` already exposes `SentMessages`.

- [ ] **Step 7: Commit**

```powershell
git add Hoshi\ViewModels\Components\NovelBookItemViewModel.cs Hoshi\ViewModels\Pages\NovelLibraryPageViewModel.cs Hoshi\App.xaml.cs Hoshi.Tests\ViewModels\Pages\NovelLibraryPageViewModelTests.cs
git commit -m "feat(novels): add novel library view model"
```

---

### Task 6: Add Novel Pages and Navigation

**Files:**
- Create: `Hoshi/Views/Pages/NovelLibraryPage.xaml`
- Create: `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`
- Create: `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`
- Create: `Hoshi/Views/Pages/NovelReaderPage.xaml`
- Create: `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`
- Modify: `Hoshi/App.xaml.cs`
- Modify: `Hoshi/Enums/AppMode.cs`
- Modify: `Hoshi/Enums/AppPage.cs`
- Modify: `Hoshi/Services/NavigationService.cs`
- Modify: `Hoshi/Views/Pages/NavigationPage.xaml`
- Modify: `Hoshi/Views/Pages/ShellPage.xaml.cs`

- [ ] **Step 1: Add AppMode**

Modify `Hoshi/Enums/AppMode.cs`:

```csharp
namespace Hoshi.Enums;

public enum AppMode
{
    Navigation,
    Reader,
    NovelReader,
}
```

- [ ] **Step 2: Add AppPage value**

Modify `Hoshi/Enums/AppPage.cs` by adding:

```csharp
NovelLibraryPage,
```

- [ ] **Step 3: Add reader ViewModel**

Create `Hoshi/ViewModels/Pages/NovelReaderPageViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Enums;
using Hoshi.Messages;
using Hoshi.Models;
using Hoshi.Models.DTO;
using Hoshi.Services.Novels;
using Hoshi.Services.UI;

namespace Hoshi.ViewModels.Pages;

public partial class NovelReaderPageViewModel : ObservableObject
{
    private readonly INovelLibraryService _novelLibraryService;
    private readonly INotificationService _notificationService;
    private readonly IMessenger _messenger;

    [ObservableProperty]
    public partial NovelBook? CurrentBook { get; set; }

    public string ReaderTitle => CurrentBook?.Title ?? "Novel reader";

    public NovelReaderPageViewModel(
        INovelLibraryService novelLibraryService,
        INotificationService notificationService,
        IMessenger messenger
    )
    {
        _novelLibraryService = novelLibraryService;
        _notificationService = notificationService;
        _messenger = messenger;
    }

    public async Task InitializeAsync(NovelReaderNavigationArgs args)
    {
        var result = await _novelLibraryService.GetNovelBookAsync(args.BookId);
        if (!result.IsSuccess)
        {
            _notificationService.ShowError(result.Error!, result.ErrorTitle!);
            return;
        }

        CurrentBook = result.Value;
        OnPropertyChanged(nameof(ReaderTitle));
        if (CurrentBook != null)
            await _novelLibraryService.MarkOpenedAsync(CurrentBook.Id);
    }

    [RelayCommand]
    private void BackToLibrary() =>
        _messenger.Send(new SwitchAppModeMessage(AppMode.Navigation, null));
}
```

- [ ] **Step 4: Add Novel library XAML**

Create `Hoshi/Views/Pages/NovelLibraryPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Page x:Class="Hoshi.Views.Pages.NovelLibraryPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:converters="using:Hoshi.Helpers.UI.Converters"
      xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
      xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
      xmlns:ui="using:CommunityToolkit.WinUI"
      xmlns:vmc="using:Hoshi.ViewModels.Components"
      mc:Ignorable="d">

    <Page.Resources>
        <converters:BooleanToVisibilityConverter x:Key="BooleanToVisibilityConverter" />
        <DataTemplate x:Key="NovelBookTemplate" x:DataType="vmc:NovelBookItemViewModel">
            <Grid Width="220"
                  Height="96"
                  Padding="12"
                  Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"
                  CornerRadius="8">
                <StackPanel Spacing="4">
                    <TextBlock Text="{x:Bind Book.Title, Mode=OneTime}"
                               TextTrimming="CharacterEllipsis"
                               Style="{StaticResource BodyStrongTextBlockStyle}" />
                    <TextBlock Text="{x:Bind Book.Author, Mode=OneTime}"
                               TextTrimming="CharacterEllipsis"
                               Style="{StaticResource CaptionTextBlockStyle}" />
                    <TextBlock Text="{x:Bind Book.FilePath, Mode=OneTime}"
                               TextTrimming="CharacterEllipsis"
                               Style="{StaticResource CaptionTextBlockStyle}" />
                </StackPanel>
            </Grid>
        </DataTemplate>
    </Page.Resources>

    <Grid RowDefinitions="Auto, *">
        <Grid Grid.Row="0" Padding="20,14,36,16" ColumnDefinitions="Auto, *, Auto">
            <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
                <FontIcon FontSize="24" Glyph="&#xE82D;" />
                <TextBlock Margin="8,0,0,0"
                           VerticalAlignment="Center"
                           Style="{StaticResource TitleTextBlockStyle}"
                           Text="Novels" />
            </StackPanel>

            <Button Grid.Column="2"
                    Command="{x:Bind ViewModel.ImportNovelCommand}"
                    ToolTipService.ToolTip="Import EPUB">
                <Button.Content>
                    <FontIcon Glyph="&#xE8E5;" />
                </Button.Content>
            </Button>
        </Grid>

        <ProgressRing Grid.Row="1"
                      Width="60"
                      Height="60"
                      HorizontalAlignment="Center"
                      VerticalAlignment="Center"
                      IsActive="{x:Bind ViewModel.IsContentLoading, Mode=OneWay}"
                      Visibility="{x:Bind ViewModel.IsContentLoading, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}" />

        <TextBlock Grid.Row="1"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   Style="{StaticResource BodyLargeStrongTextBlockStyle}"
                   Text="No novels imported."
                   Visibility="{x:Bind ViewModel.NoNovels, Converter={StaticResource BooleanToVisibilityConverter}, Mode=OneWay}" />

        <GridView Grid.Row="1"
                  Padding="0,0,16,0"
                  IsItemClickEnabled="True"
                  ItemClick="GridView_ItemClick"
                  ItemTemplate="{StaticResource NovelBookTemplate}"
                  ItemsSource="{x:Bind ViewModel.NovelBooks, Mode=OneWay}"
                  SelectionMode="None" />
    </Grid>
</Page>
```

- [ ] **Step 5: Add Novel library code-behind**

Create `Hoshi/Views/Pages/NovelLibraryPage.xaml.cs`:

```csharp
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.ViewModels.Components;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class NovelLibraryPage : Page
{
    public NovelLibraryPageViewModel ViewModel { get; set; }

    public NovelLibraryPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NovelLibraryPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.InitializeAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private void GridView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NovelBookItemViewModel novelItem)
            ViewModel.OpenNovelCommand.Execute(novelItem);
    }
}
```

- [ ] **Step 6: Add reader placeholder XAML and code-behind**

Create `Hoshi/Views/Pages/NovelReaderPage.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<Page x:Class="Hoshi.Views.Pages.NovelReaderPage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
      xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
      xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
      mc:Ignorable="d">

    <Grid RowDefinitions="Auto,*" Padding="16">
        <Button Command="{x:Bind ViewModel.BackToLibraryCommand}" HorizontalAlignment="Left">
            <Button.Content>
                <FontIcon Glyph="&#xE72B;" />
            </Button.Content>
        </Button>

        <StackPanel Grid.Row="1"
                    HorizontalAlignment="Center"
                    VerticalAlignment="Center"
                    Spacing="8">
            <TextBlock Text="{x:Bind ViewModel.ReaderTitle, Mode=OneWay}"
                       Style="{StaticResource TitleTextBlockStyle}"
                       HorizontalAlignment="Center" />
            <TextBlock Text="EPUB reader host will be added in the next slice."
                       Style="{StaticResource BodyTextBlockStyle}"
                       HorizontalAlignment="Center" />
        </StackPanel>
    </Grid>
</Page>
```

Create `Hoshi/Views/Pages/NovelReaderPage.xaml.cs`:

```csharp
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Hoshi.Models.DTO;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class NovelReaderPage : Page
{
    public NovelReaderPageViewModel ViewModel { get; set; }

    public NovelReaderPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<NovelReaderPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is NovelReaderNavigationArgs args)
            await ViewModel.InitializeAsync(args);
    }
}
```

- [ ] **Step 7: Register reader ViewModel**

Modify `Hoshi/App.xaml.cs`:

```csharp
services.AddTransient<NovelReaderPageViewModel>();
```

- [ ] **Step 8: Add navigation item**

Modify `Hoshi/Views/Pages/NavigationPage.xaml` inside `NavigationView.MenuItems`:

```xml
<NavigationViewItem Content="Novels" Icon="Book" Tag="Hoshi.Views.Pages.NovelLibraryPage" />
```

- [ ] **Step 9: Recognize Novel page in navigation service**

Modify `Hoshi/Services/NavigationService.cs` switch:

```csharp
Type t when t == typeof(NovelLibraryPage) => AppPage.NovelLibraryPage,
```

- [ ] **Step 10: Route novel reader mode**

Modify `Hoshi/Views/Pages/ShellPage.xaml.cs`:

```csharp
var pageType = m.appMode switch
{
    AppMode.Reader => typeof(ReaderPage),
    AppMode.NovelReader => typeof(NovelReaderPage),
    _ => typeof(NavigationPage),
};
```

Use right-slide transition for both `Reader` and `NovelReader`:

```csharp
var isReaderMode = m.appMode is AppMode.Reader or AppMode.NovelReader;
```

- [ ] **Step 11: Build**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\Hoshi.slnx -c Debug -p:Platform=x64 --no-restore
```

Expected: build succeeds.

- [ ] **Step 12: Commit**

```powershell
git add Hoshi\Views\Pages\NovelLibraryPage.xaml Hoshi\Views\Pages\NovelLibraryPage.xaml.cs Hoshi\ViewModels\Pages\NovelReaderPageViewModel.cs Hoshi\Views\Pages\NovelReaderPage.xaml Hoshi\Views\Pages\NovelReaderPage.xaml.cs Hoshi\App.xaml.cs Hoshi\Enums\AppMode.cs Hoshi\Enums\AppPage.cs Hoshi\Services\NavigationService.cs Hoshi\Views\Pages\NavigationPage.xaml Hoshi\Views\Pages\ShellPage.xaml.cs
git commit -m "feat(novels): add novel library navigation"
```

---

### Task 7: Final Verification

**Files:**
- No new files.

- [ ] **Step 1: Restore with proxy**

Run:

```powershell
$env:HTTP_PROXY='http://127.0.0.1:7890'
$env:HTTPS_PROXY='http://127.0.0.1:7890'
& 'C:\Program Files\dotnet\dotnet.exe' restore .\Hoshi.slnx -r win-x64
```

Expected: restore succeeds.

- [ ] **Step 2: Build**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' build .\Hoshi.slnx -c Debug -p:Platform=x64 --no-restore
```

Expected: build succeeds. Existing nullable warnings may remain unless introduced warnings point to new Novel code.

- [ ] **Step 3: Run tests**

Run:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' test .\Hoshi.slnx -c Debug -p:Platform=x64 --no-build --logger "console;verbosity=minimal"
```

Expected: all tests pass.

- [ ] **Step 4: Launch app**

Run:

```powershell
Start-Process -FilePath 'D:\CODE\Hoshi\Hoshi\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\Hoshi.exe' -WorkingDirectory 'D:\CODE\Hoshi\Hoshi\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64'
Start-Sleep -Seconds 5
Get-Process Hoshi -ErrorAction SilentlyContinue | Select-Object Id,ProcessName,Responding,StartTime
```

Expected: `Hoshi` process exists and `Responding` is `True`.

- [ ] **Step 5: Manual UI smoke test**

In the running app:

1. Open the new `Novels` navigation item.
2. Click import.
3. Cancel the picker and verify no error notification appears.
4. Import a small `.epub`.
5. Verify it appears in the Novel library list.
6. Click it and verify the Novel reader placeholder opens.
7. Return to the library.
8. Verify existing `Favorites` and `Discover` still open.

- [ ] **Step 6: Commit smoke-test navigation fix when required**

If manual smoke testing shows that the Novel navigation item opens but selection state does not update, make the navigation recognition fix and commit these exact files:

```powershell
git add Hoshi\Views\Pages\NavigationPage.xaml Hoshi\Services\NavigationService.cs
git commit -m "fix(novels): complete novel library smoke fixes"
```

Expected: skip this commit when the smoke test already passes.
