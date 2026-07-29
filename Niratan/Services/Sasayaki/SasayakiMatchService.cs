using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Sasayaki;
using Niratan.Services.Novels;

namespace Niratan.Services.Sasayaki;

public sealed class SasayakiMatchService : ISasayakiMatchService
{
    private const int AudiobookProbeLength = 4096;

    private readonly IEpubParserService _epubParserService;
    private readonly ISasayakiSidecarService _sidecarService;
    private readonly SasayakiParser _parser = new();
    private readonly SasayakiMatcher _matcher = new();

    public SasayakiMatchService(
        IEpubParserService epubParserService,
        ISasayakiSidecarService sidecarService)
    {
        _epubParserService = epubParserService;
        _sidecarService = sidecarService;
    }

    public async Task<SasayakiMatchData> MatchAsync(
        NovelBook book,
        string audiobookPath,
        string srtPath,
        int searchWindow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        cancellationToken.ThrowIfCancellationRequested();

        var audiobookHasDataTask = HasReadableAudiobookHeaderAsync(
            audiobookPath,
            cancellationToken);
        var cuesTask = _parser.ParseAsync(srtPath, cancellationToken);
        await Task.WhenAll(audiobookHasDataTask, cuesTask);
        var audiobookHasData = await audiobookHasDataTask;
        var cues = await cuesTask;
        ThrowIfResourcesAreUnreadable(audiobookHasData, cues.Count);

        var bookRootPath = string.IsNullOrWhiteSpace(book.ExtractedPath)
            ? AppDataHelper.GetNovelBookPath(book.Id)
            : book.ExtractedPath;
        var epubBook = _epubParserService.Parse(book.FilePath, bookRootPath);
        cancellationToken.ThrowIfCancellationRequested();

        var matchData = await _matcher.MatchAsync(
            epubBook,
            cues,
            searchWindow);
        cancellationToken.ThrowIfCancellationRequested();

        await _sidecarService.SaveMatchAsync(bookRootPath, matchData, cancellationToken);
        await _sidecarService.SaveSourceAsync(
            bookRootPath,
            new SasayakiSourceData
            {
                AudiobookPath = audiobookPath,
                SrtPath = srtPath,
            },
            cancellationToken);
        return matchData;
    }

    private static async Task<bool> HasReadableAudiobookHeaderAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            AudiobookProbeLength,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0)
            return false;

        var buffer = new byte[(int)Math.Min(AudiobookProbeLength, stream.Length)];
        var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
        for (var i = 0; i < bytesRead; i++)
        {
            if (buffer[i] != 0)
                return true;
        }

        return false;
    }

    private static void ThrowIfResourcesAreUnreadable(
        bool audiobookHasData,
        int cueCount)
    {
        if (!audiobookHasData && cueCount == 0)
        {
            throw new SasayakiMatchInputException(
                SasayakiMatchInputError.UnreadableAudiobookAndSubtitle);
        }

        if (!audiobookHasData)
        {
            throw new SasayakiMatchInputException(
                SasayakiMatchInputError.UnreadableAudiobook);
        }

        if (cueCount == 0)
        {
            throw new SasayakiMatchInputException(
                SasayakiMatchInputError.InvalidSubtitle);
        }
    }
}
