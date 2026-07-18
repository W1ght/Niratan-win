using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

public interface IReaderImageGalleryService
{
    Task<IReadOnlyList<ReaderGalleryImage>> LoadImagesAsync(
        EpubBook book,
        IReadOnlyList<string>? cachedRelativePaths = null,
        CancellationToken ct = default);
}
