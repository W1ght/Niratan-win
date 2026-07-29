using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Sasayaki;

namespace Niratan.Services.Sasayaki;

public enum SasayakiMatchInputError
{
    UnreadableAudiobook,
    InvalidSubtitle,
    UnreadableAudiobookAndSubtitle,
}

public sealed class SasayakiMatchInputException(SasayakiMatchInputError error)
    : Exception(GetMessage(error))
{
    public SasayakiMatchInputError Error { get; } = error;

    private static string GetMessage(SasayakiMatchInputError error) => error switch
    {
        SasayakiMatchInputError.UnreadableAudiobook =>
            "The selected audiobook contains no readable media data.",
        SasayakiMatchInputError.InvalidSubtitle =>
            "The selected subtitle contains no valid SRT cues.",
        SasayakiMatchInputError.UnreadableAudiobookAndSubtitle =>
            "The selected audiobook and subtitle contain no readable data.",
        _ => "The selected Sasayaki resources are invalid.",
    };
}

public interface ISasayakiMatchService
{
    Task<SasayakiMatchData> MatchAsync(
        NovelBook book,
        string audiobookPath,
        string srtPath,
        int searchWindow,
        CancellationToken cancellationToken = default);
}
