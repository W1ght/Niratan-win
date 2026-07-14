using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.DTO;

namespace Niratan.Services.Novels;

public interface INovelEpubImportService
{
    Task<Result<NovelImportResult>> ImportAsync(string filePath, CancellationToken ct = default);
}
