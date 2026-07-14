using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;

namespace Niratan.Services.UI;

public interface IFileRevealService
{
    Task<Result> RevealInFileExplorerAsync(string filePath, CancellationToken ct = default);
}
