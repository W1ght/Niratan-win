using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Common;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public interface INovelShelfService
{
    Task<Result<NovelShelfState>> LoadAsync(CancellationToken ct = default);
    Task<Result<NovelShelfState>> CreateAsync(string name, CancellationToken ct = default);
    Task<Result<NovelShelfState>> RenameAsync(
        string oldName,
        string newName,
        CancellationToken ct = default);
    Task<Result<NovelShelfState>> ReorderShelvesAsync(
        IReadOnlyList<string> names,
        CancellationToken ct = default);
    Task<Result<NovelShelfState>> DeleteAsync(string name, CancellationToken ct = default);
    Task<Result<NovelShelfState>> MoveBooksAsync(
        IReadOnlyList<string> bookIds,
        string? targetShelf,
        CancellationToken ct = default);
    Task<Result<NovelShelfState>> ReorderBookAsync(
        string sourceId,
        string targetId,
        string? shelf,
        CancellationToken ct = default);
    Task<Result<NovelShelfState>> RemoveBookAsync(
        string bookId,
        CancellationToken ct = default);
}
