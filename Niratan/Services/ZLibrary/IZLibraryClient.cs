using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.ZLibrary;

namespace Niratan.Services.ZLibrary;

public interface IZLibraryClient
{
    Task<ZLibrarySession> LoginAsync(
        ZLibraryCredentials credentials,
        CancellationToken ct = default);

    Task<ZLibrarySearchResult> SearchAsync(
        ZLibrarySession session,
        ZLibrarySearchOptions options,
        int page = 1,
        CancellationToken ct = default);

    Task DownloadEpubAsync(
        ZLibrarySession session,
        ZLibraryBook book,
        Stream destination,
        CancellationToken ct = default);
}

public sealed class ZLibraryException : Exception
{
    public ZLibraryException(string message) : base(message)
    {
    }

    public ZLibraryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
