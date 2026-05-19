using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Common;
using Hoshi.Models.DTO;

namespace Hoshi.Services.Novels;

public interface INovelEpubImportService
{
    Task<Result<NovelImportResult>> ImportAsync(string filePath, CancellationToken ct = default);
}
