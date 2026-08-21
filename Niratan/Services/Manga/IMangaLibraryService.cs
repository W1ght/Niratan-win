using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Manga;

namespace Niratan.Services.Manga;

public interface IMangaLibraryService
{
    Task<Result<IReadOnlyList<MangaBook>>> GetBooksAsync(CancellationToken ct = default);
    Task<Result<MangaBook>> ImportAsync(string sourcePath, CancellationToken ct = default);
    Task<Result<MangaBook?>> GetBookAsync(string bookId, CancellationToken ct = default);
    Task<Result<MangaReaderSession?>> CreateReaderSessionAsync(
        string bookId,
        CancellationToken ct = default);
    Task<Result<MangaReaderSession>> CreateReaderSessionAsync(
        MangaBook book,
        CancellationToken ct = default);
    Task<Result> SaveProgressAsync(string bookId, int pageIndex, CancellationToken ct = default);
    Task<Result> SaveReaderPreferencesAsync(
        MangaReaderPreferences preferences,
        CancellationToken ct = default);
    Task<Result> RenameAsync(string bookId, string title, CancellationToken ct = default);
    Task<Result> MarkReadAsync(string bookId, CancellationToken ct = default);
    Task<Result> RemoveAsync(string bookId, CancellationToken ct = default);
}

public interface IMangaPageProvider
{
    Task<string> GetPagePathAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default);
}

public interface IMangaTextRegionService
{
    Task<IReadOnlyList<MangaTextRegion>> GetRegionsAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default);
}

public interface IMangaOcrService
{
    Task<IReadOnlyList<MangaTextRegion>?> GetCachedRegionsAsync(
        MangaOcrCacheKey key,
        IReadOnlyList<string> pageIdentities,
        CancellationToken ct = default);

    Task<IReadOnlyList<MangaTextRegion>> RecognizeAsync(
        string imagePath,
        MangaOcrCacheKey key,
        IReadOnlyList<string> pageIdentities,
        CancellationToken ct = default);
}

public interface IMangaReaderWindowService
{
    event System.EventHandler? LibraryChanged;
    Task OpenAsync(MangaBook book, CancellationToken ct = default);
}

public interface ISuwayomiService
{
    Task<SuwayomiServerConfiguration> LoadConfigurationAsync(
        CancellationToken ct = default);
    Task SaveConfigurationAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        CancellationToken ct = default);
    Task<IReadOnlyList<SuwayomiSource>> ConnectAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        CancellationToken ct = default);
    Task<SuwayomiPagedManga> BrowseAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        string sourceId,
        string? query,
        int page,
        CancellationToken ct = default);
    Task<IReadOnlyList<SuwayomiManga>> GetLibraryAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        CancellationToken ct = default);
    Task<SuwayomiManga> GetMangaDetailsAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        CancellationToken ct = default);
    Task SetLibraryAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        bool isInLibrary,
        CancellationToken ct = default);
    Task<string> GetThumbnailPathAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        CancellationToken ct = default);
    Task<string?> GetSourceIconPathAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        SuwayomiSource source,
        CancellationToken ct = default);
    Task<IReadOnlyList<SuwayomiChapter>> GetChaptersAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        int mangaId,
        CancellationToken ct = default);
    Task<MangaBook> CreateReaderBookAsync(
        SuwayomiServerConfiguration configuration,
        string? secret,
        SuwayomiManga manga,
        SuwayomiChapter chapter,
        CancellationToken ct = default);
    Task<string> GetPagePathAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default);
    Task UpdateProgressAsync(
        MangaBook book,
        int pageIndex,
        bool completed,
        CancellationToken ct = default);
}

public interface IMihonExtensionService
{
    Task<MihonExtensionConfiguration> LoadConfigurationAsync(
        CancellationToken ct = default);
    Task SaveConfigurationAsync(
        MihonExtensionConfiguration configuration,
        CancellationToken ct = default);
    Task ConnectAsync(
        MihonExtensionConfiguration configuration,
        CancellationToken ct = default);
    Task<MihonRepositoryRefreshResult> RefreshRepositoriesAsync(
        MihonExtensionConfiguration configuration,
        CancellationToken ct = default);
    Task<IReadOnlyList<MihonInstalledExtension>> GetInstalledSourcesAsync(
        CancellationToken ct = default);
    Task RemoveAsync(
        string packageName,
        string sourceId,
        CancellationToken ct = default);
    Task<string?> GetRepositorySourceIconPathAsync(
        MihonExtensionConfiguration configuration,
        MihonExtensionSource source,
        CancellationToken ct = default);
    Task<MihonInstalledExtension> InstallAsync(
        MihonExtensionConfiguration configuration,
        MihonExtensionSource source,
        CancellationToken ct = default);
    Task<MihonPagedManga> BrowseAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        string? query,
        int page,
        CancellationToken ct = default);
    Task<MihonManga> GetMangaDetailsAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct = default);
    Task<IReadOnlyList<MihonChapter>> GetChaptersAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct = default);
    Task<MangaBook> CreateReaderBookAsync(
        MihonExtensionConfiguration configuration,
        MihonInstalledExtension source,
        MihonManga manga,
        MihonChapter chapter,
        CancellationToken ct = default);
    Task<string> GetThumbnailPathAsync(
        MihonInstalledExtension source,
        MihonManga manga,
        CancellationToken ct = default);
    Task<string> GetPagePathAsync(
        MangaBook book,
        int pageIndex,
        CancellationToken ct = default);
}
