using System;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Sasayaki;
using Hoshi.Services.Novels;

namespace Hoshi.Services.Sasayaki;

public sealed class SasayakiMatchService : ISasayakiMatchService
{
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

        var bookRootPath = string.IsNullOrWhiteSpace(book.ExtractedPath)
            ? AppDataHelper.GetNovelBookPath(book.Id)
            : book.ExtractedPath;
        var epubBook = _epubParserService.Parse(book.FilePath, bookRootPath);
        cancellationToken.ThrowIfCancellationRequested();

        var cues = await _parser.ParseAsync(srtPath);
        cancellationToken.ThrowIfCancellationRequested();

        var matchData = await _matcher.MatchAsync(
            epubBook,
            cues,
            book.Id,
            audiobookPath,
            srtPath,
            searchWindow);
        cancellationToken.ThrowIfCancellationRequested();

        await _sidecarService.SaveMatchAsync(bookRootPath, matchData, cancellationToken);
        await _sidecarService.SavePlaybackAsync(bookRootPath, new SasayakiPlaybackData(), cancellationToken);
        return matchData;
    }
}
