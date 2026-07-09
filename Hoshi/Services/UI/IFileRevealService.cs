using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Common;

namespace Hoshi.Services.UI;

public interface IFileRevealService
{
    Task<Result> RevealInFileExplorerAsync(string filePath, CancellationToken ct = default);
}
