using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.ZLibrary;

namespace Niratan.Services.ZLibrary;

public interface IZLibraryService
{
    bool HasCredentials { get; }

    Task<ZLibraryCredentials?> LoadCredentialsAsync(CancellationToken ct = default);

    Task<Result> ConnectAsync(
        ZLibraryCredentials credentials,
        CancellationToken ct = default);

    Task<Result> DisconnectAsync(CancellationToken ct = default);

    Task<Result<ZLibrarySearchResult>> SearchAsync(
        ZLibrarySearchOptions options,
        int page = 1,
        CancellationToken ct = default);

    Task<Result<NovelBook>> DownloadAndImportAsync(
        ZLibraryBook book,
        CancellationToken ct = default);
}
