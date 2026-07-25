using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.ZLibrary;
using Niratan.Services.Novels;

namespace Niratan.Services.ZLibrary;

public sealed class ZLibraryService : IZLibraryService
{
    private readonly IZLibraryClient _client;
    private readonly IZLibraryCredentialStore _credentialStore;
    private readonly INovelLibraryService _novelLibrary;
    private readonly ILogger<ZLibraryService> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private ZLibrarySession? _session;

    public ZLibraryService(
        IZLibraryClient client,
        IZLibraryCredentialStore credentialStore,
        INovelLibraryService novelLibrary,
        ILogger<ZLibraryService> logger)
    {
        _client = client;
        _credentialStore = credentialStore;
        _novelLibrary = novelLibrary;
        _logger = logger;
    }

    public bool HasCredentials => _credentialStore.HasCredentials;

    public Task<ZLibraryCredentials?> LoadCredentialsAsync(CancellationToken ct = default) =>
        _credentialStore.LoadAsync(ct);

    public async Task<Result> ConnectAsync(
        ZLibraryCredentials credentials,
        CancellationToken ct = default)
    {
        try
        {
            var session = await _client.LoginAsync(credentials, ct);
            await _credentialStore.SaveAsync(
                credentials with { BaseUrl = session.BaseUri.GetLeftPart(UriPartial.Authority) },
                ct);
            _session = session;
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex) when (ex is ZLibraryException or IOException)
        {
            _logger.LogWarning(ex, "Could not connect to Z-Library");
            return Result.Failure(ex.Message, "Z-Library sign-in failed");
        }
    }

    public async Task<Result> DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            _session = null;
            await _credentialStore.DeleteAsync(ct);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not remove Z-Library credentials");
            return Result.Failure(ex.Message, "Z-Library sign-out failed");
        }
    }

    public async Task<Result<ZLibrarySearchResult>> SearchAsync(
        ZLibrarySearchOptions options,
        int page = 1,
        CancellationToken ct = default)
    {
        try
        {
            var session = await EnsureSessionAsync(ct);
            var result = await _client.SearchAsync(session, options, page, ct);
            return Result<ZLibrarySearchResult>.Success(result);
        }
        catch (OperationCanceledException)
        {
            return Result<ZLibrarySearchResult>.Cancelled();
        }
        catch (Exception ex) when (ex is ZLibraryException or IOException)
        {
            _logger.LogWarning(ex, "Z-Library search failed");
            return Result<ZLibrarySearchResult>.Failure(ex.Message, "Z-Library search failed");
        }
    }

    public async Task<Result<NovelBook>> DownloadAndImportAsync(
        ZLibraryBook book,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        var downloadDirectory = Path.Combine(
            Path.GetTempPath(),
            "Niratan",
            "ZLibraryDownloads");
        var temporaryPath = Path.Combine(downloadDirectory, Guid.NewGuid().ToString("N") + ".epub");

        try
        {
            Directory.CreateDirectory(downloadDirectory);
            var session = await EnsureSessionAsync(ct);
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _client.DownloadEpubAsync(session, book, output, ct);
                await output.FlushAsync(ct);
            }

            ValidateEpubContainer(temporaryPath);
            var importResult = await _novelLibrary.ImportEpubAsync(temporaryPath, ct);
            if (!importResult.IsSuccess)
            {
                return importResult.IsCancelled
                    ? Result<NovelBook>.Cancelled()
                    : Result<NovelBook>.Failure(
                        importResult.Error ?? "The downloaded EPUB could not be imported.",
                        importResult.ErrorTitle ?? "Z-Library import failed");
            }

            _logger.LogInformation(
                "Imported Z-Library EPUB {BookId} as novel {NovelId}",
                book.Id,
                importResult.Value!.Id);
            return importResult;
        }
        catch (OperationCanceledException)
        {
            return Result<NovelBook>.Cancelled();
        }
        catch (Exception ex) when (ex is ZLibraryException or IOException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Z-Library download/import failed for book {BookId}", book.Id);
            return Result<NovelBook>.Failure(ex.Message, "Z-Library import failed");
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private async Task<ZLibrarySession> EnsureSessionAsync(CancellationToken ct)
    {
        if (_session is not null)
            return _session;

        await _sessionGate.WaitAsync(ct);
        try
        {
            if (_session is not null)
                return _session;

            var credentials = await _credentialStore.LoadAsync(ct)
                ?? throw new ZLibraryException("Connect a Z-Library account before searching.");
            _session = await _client.LoginAsync(credentials, ct);
            return _session;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private static void ValidateEpubContainer(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 4)
            throw new InvalidDataException("The downloaded file is empty or incomplete.");

        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length
            || signature[0] != (byte)'P'
            || signature[1] != (byte)'K')
        {
            throw new InvalidDataException("The downloaded file is not an EPUB archive.");
        }

        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.GetEntry("META-INF/container.xml") is null)
            throw new InvalidDataException("The downloaded archive is missing the EPUB container metadata.");

        var mimetype = archive.GetEntry("mimetype");
        if (mimetype is null)
            throw new InvalidDataException("The downloaded archive is missing its EPUB media type.");
        using var reader = new StreamReader(mimetype.Open());
        if (!string.Equals(
                reader.ReadToEnd().Trim(),
                "application/epub+zip",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("The downloaded archive has an invalid EPUB media type.");
        }
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
