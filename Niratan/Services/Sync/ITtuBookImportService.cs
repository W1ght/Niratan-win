using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models;
using Niratan.Models.Common;
using Niratan.Models.Sync;

namespace Niratan.Services.Sync;

public interface ITtuBookDataConverter
{
    Task<string> ConvertToEpubAsync(
        string ttuBookDataPath,
        string outputDirectory,
        CancellationToken ct = default);
}

public interface ITtuBackupBookDataConverter : ITtuBookDataConverter
{
    Task<string> ReadTitleAsync(
        string ttuBookDataPath,
        CancellationToken ct = default);

    Task<string> ConvertFromEpubAsync(
        NovelBook book,
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
