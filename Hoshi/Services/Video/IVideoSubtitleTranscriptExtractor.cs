using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Video;

public interface IVideoSubtitleTranscriptExtractor
{
    Task<IReadOnlyList<VideoSubtitleCue>> ExtractAsync(
        string videoPath,
        VideoTrackInfo track,
        CancellationToken ct = default);
}
