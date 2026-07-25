using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public interface IResourcePackageImportService
{
    ResourcePackageAnalysis Analyze(string rootPath);

    Task<Result<ResourcePackageImportResult>> ImportAsync(
        string rootPath,
        CancellationToken ct = default);
}
