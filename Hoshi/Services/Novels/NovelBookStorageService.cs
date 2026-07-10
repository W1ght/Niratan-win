using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

internal sealed class NovelBookStorageService : INovelBookStorageService
{
    private const string MetadataFileName = "metadata.json";
    private const string BookOrderFileName = "book_order.json";

    private readonly string _booksRoot;
    private readonly INiratanJsonFileStore _json;
    private readonly INovelBookSidecarService _sidecars;

    public NovelBookStorageService(
        INiratanJsonFileStore json,
        INovelBookSidecarService sidecars)
        : this(AppDataHelper.GetNovelBooksPath(), json, sidecars)
    {
    }

    internal NovelBookStorageService(
        string booksRoot,
        INiratanJsonFileStore json,
        INovelBookSidecarService sidecars)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(booksRoot);
        _booksRoot = Path.GetFullPath(booksRoot);
        _json = json ?? throw new ArgumentNullException(nameof(json));
        _sidecars = sidecars ?? throw new ArgumentNullException(nameof(sidecars));
        Directory.CreateDirectory(_booksRoot);
    }

    public async Task<NovelBookCatalogSnapshot> LoadSnapshotAsync(
        string? queryText = null,
        CancellationToken ct = default)
    {
        var books = new List<NovelBook>();
        var corruptMetadataPaths = new List<string>();

        foreach (var directory in Directory.EnumerateDirectories(_booksRoot))
        {
            ct.ThrowIfCancellationRequested();
            var metadataPath = Path.Combine(directory, MetadataFileName);
            var metadataResult = await _json.ReadAsync<NovelBookMetadata>(metadataPath, ct);
            if (metadataResult.Status == NovelJsonReadStatus.Invalid)
            {
                corruptMetadataPaths.Add(metadataPath);
                continue;
            }

            if (metadataResult.Status == NovelJsonReadStatus.Missing)
            {
                continue;
            }

            var metadata = metadataResult.Value;
            if (metadata is null || !IsValidMetadata(metadata))
            {
                corruptMetadataPaths.Add(metadataPath);
                continue;
            }

            if (!MatchesQuery(metadata, queryText))
                continue;

            var bookmark = await _sidecars.LoadBookmarkAsync(directory, ct);
            var bookInfo = await _sidecars.LoadBookInfoAsync(directory, ct);
            books.Add(ToDomain(metadata, directory, bookmark, bookInfo));
        }

        var sortedBooks = books
            .OrderByDescending(book => book.LastOpenedAt ?? book.ImportedAt)
            .ThenBy(book => book.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new NovelBookCatalogSnapshot(sortedBooks, corruptMetadataPaths);
    }

    public async Task<NovelBook?> LoadAsync(
        string bookId,
        CancellationToken ct = default)
    {
        ValidatePathSegment(bookId, nameof(bookId));
        var snapshot = await LoadSnapshotAsync(ct: ct);
        return snapshot.Books.FirstOrDefault(
            book => string.Equals(book.Id, bookId, StringComparison.Ordinal));
    }

    public async Task SaveMetadataAsync(
        NovelBook book,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ValidatePathSegment(book.Id, nameof(book.Id));
        var folder = string.IsNullOrWhiteSpace(book.Folder) ? book.Id : book.Folder;
        ValidatePathSegment(folder, nameof(book.Folder));
        var root = ResolveRootPath(folder);
        Directory.CreateDirectory(root);

        var metadata = new NovelBookMetadata(
            book.Id,
            string.IsNullOrWhiteSpace(book.OriginalTitle) ? book.Title : book.OriginalTitle,
            ToMetadataPath(root, book.FilePath),
            ToMetadataPath(root, book.CoverPath),
            folder,
            ToDateTimeOffset(book.LastOpenedAt ?? book.ImportedAt),
            book.RenamedTitle,
            book.ProfileId,
            book.Language);
        await _json.WriteAsync(Path.Combine(root, MetadataFileName), metadata, ct);
    }

    public async Task UpdateLastAccessAsync(
        string bookId,
        DateTimeOffset lastAccess,
        CancellationToken ct = default)
    {
        var book = await LoadRequiredAsync(bookId, ct);
        book.LastOpenedAt = lastAccess.UtcDateTime;
        await SaveMetadataAsync(book, ct);
    }

    public async Task UpdateProfileAsync(
        string bookId,
        string? profileId,
        CancellationToken ct = default)
    {
        var book = await LoadRequiredAsync(bookId, ct);
        book.ProfileId = profileId;
        await SaveMetadataAsync(book, ct);
    }

    public async Task<IReadOnlyList<string>> LoadBookOrderAsync(
        CancellationToken ct = default)
    {
        var result = await _json.ReadAsync<List<string>>(
            Path.Combine(_booksRoot, BookOrderFileName),
            ct);
        return result.Status switch
        {
            NovelJsonReadStatus.Missing => [],
            NovelJsonReadStatus.Success when result.Value is not null => result.Value,
            _ => throw new InvalidDataException(
                result.Error ?? $"Invalid {BookOrderFileName}."),
        };
    }

    public Task SaveBookOrderAsync(
        IReadOnlyList<string> orderedBookIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(orderedBookIds);
        return _json.WriteAsync(
            Path.Combine(_booksRoot, BookOrderFileName),
            orderedBookIds.ToList(),
            ct);
    }

    public async Task DeleteAsync(string bookId, CancellationToken ct = default)
    {
        ValidatePathSegment(bookId, nameof(bookId));
        var book = await LoadAsync(bookId, ct);
        var root = book?.ExtractedPath ?? ResolveRootPath(bookId);
        EnsureDirectChild(root);
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    public string ResolveRootPath(string bookId)
    {
        ValidatePathSegment(bookId, nameof(bookId));
        var path = Path.GetFullPath(Path.Combine(_booksRoot, bookId));
        EnsureDirectChild(path);
        return path;
    }

    private async Task<NovelBook> LoadRequiredAsync(
        string bookId,
        CancellationToken ct)
    {
        var book = await LoadAsync(bookId, ct);
        return book ?? throw new FileNotFoundException("Novel metadata was not found.", bookId);
    }

    private NovelBook ToDomain(
        NovelBookMetadata metadata,
        string directory,
        NovelBookmark? bookmark,
        NovelBookInfo? bookInfo) =>
        new()
        {
            Id = metadata.Id,
            Title = metadata.DisplayTitle,
            OriginalTitle = metadata.Title,
            RenamedTitle = metadata.RenamedTitle,
            Folder = metadata.Folder,
            FilePath = ResolveMetadataPath(directory, metadata.Epub) ?? string.Empty,
            CoverPath = ResolveMetadataPath(directory, metadata.Cover),
            ImportedAt = Directory.GetCreationTimeUtc(directory),
            LastOpenedAt = metadata.LastAccess.UtcDateTime,
            Language = metadata.BookLanguage,
            ExtractedPath = directory,
            ChapterCount = bookInfo?.ChapterInfo.Count ?? 0,
            CurrentChapterIndex = bookmark?.ChapterIndex ?? 0,
            Progress = bookmark?.Progress ?? 0,
            CurrentCharacterCount = bookmark?.CharacterCount ?? 0,
            TotalCharacterCount = bookInfo?.CharacterCount ?? 0,
            ProfileId = metadata.ProfileId,
        };

    private static bool MatchesQuery(NovelBookMetadata metadata, string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return true;

        var query = queryText.Trim();
        return metadata.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || metadata.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private static string? ResolveMetadataPath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            return null;

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        return IsContainedPath(fullRoot, candidate) ? candidate : null;
    }

    private static string? ToMetadataPath(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(fullRoot, path));
        if (!IsContainedPath(fullRoot, candidate))
            return null;

        return Path.GetRelativePath(fullRoot, candidate).Replace('\\', '/');
    }

    private static bool IsContainedPath(string root, string candidate)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private void EnsureDirectChild(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.Equals(parent, _booksRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Novel path must be a direct child of the books root.");
    }

    private static void ValidatePathSegment(string value, string parameterName)
    {
        if (!IsSafePathSegment(value))
            throw new ArgumentException("Value must be a single safe path segment.", parameterName);
    }

    private static bool IsValidMetadata(NovelBookMetadata metadata) =>
        IsSafePathSegment(metadata.Id)
        && IsSafePathSegment(metadata.Folder)
        && !string.IsNullOrWhiteSpace(metadata.Title);

    private static bool IsSafePathSegment(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value is not "." and not ".."
        && value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0
        && !Path.IsPathFullyQualified(value);

    private static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        value.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(value),
            DateTimeKind.Local => new DateTimeOffset(value).ToUniversalTime(),
            _ => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
        };
}
