using System;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models;
using Hoshi.Models.Common;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public interface ITtuBookDataConverter
{
    Task<string> ConvertToEpubAsync(
        string ttuBookDataPath,
        string outputDirectory,
        CancellationToken ct = default);
}

public interface ITtuBookImportService
{
    Task<Result<NovelBook>> ImportRemoteBookAsync(
        TtuRemoteBook remoteBook,
        TtuBookImportOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
