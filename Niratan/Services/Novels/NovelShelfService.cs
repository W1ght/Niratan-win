using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Common;
using Niratan.Models.Novel;

namespace Niratan.Services.Novels;

internal sealed class NovelShelfService : INovelShelfService
{
    private const string ShelvesFileName = "shelves.json";

    private readonly string _shelvesPath;
    private readonly INovelBookStorageService _storage;
    private readonly INiratanJsonFileStore _json;
    private readonly INovelStorageAccessState _accessState;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NovelShelfService(
        INovelBookStorageService storage,
        INiratanJsonFileStore json,
        INovelStorageAccessState accessState)
        : this(AppDataHelper.GetNovelBooksPath(), storage, json, accessState)
    {
    }

    internal NovelShelfService(
        string booksRoot,
        INovelBookStorageService storage,
        INiratanJsonFileStore json,
        INovelStorageAccessState accessState)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(booksRoot);
        _shelvesPath = Path.Combine(Path.GetFullPath(booksRoot), ShelvesFileName);
        _storage = storage;
        _json = json;
        _accessState = accessState;
    }

    public Task<Result<NovelShelfState>> LoadAsync(CancellationToken ct = default) =>
        ExecuteAsync(
            async token =>
            {
                var document = await LoadCoreAsync(token);
                if (!_accessState.IsReadOnly)
                    await SaveCoreAsync(document, token);
                return document.State;
            },
            requiresWrite: false,
            ct);

    public Task<Result<NovelShelfState>> CreateAsync(
        string name,
        CancellationToken ct = default) =>
        MutateAsync(
            document =>
            {
                var normalizedName = NormalizeName(name);
                if (document.Shelves.Any(shelf => NamesEqual(shelf.Name, normalizedName)))
                    throw new InvalidDataException("A shelf with that name already exists.");
                document.Shelves.Add(new NovelShelf(normalizedName, []));
            },
            ct);

    public Task<Result<NovelShelfState>> RenameAsync(
        string oldName,
        string newName,
        CancellationToken ct = default) =>
        MutateAsync(
            document =>
            {
                var normalizedName = NormalizeName(newName);
                var index = FindShelfIndex(document.Shelves, oldName);
                if (index < 0)
                    throw new InvalidDataException("Shelf not found.");
                if (document.Shelves
                    .Where((_, shelfIndex) => shelfIndex != index)
                    .Any(shelf => NamesEqual(shelf.Name, normalizedName)))
                {
                    throw new InvalidDataException("A shelf with that name already exists.");
                }

                document.Shelves[index] = document.Shelves[index] with
                {
                    Name = normalizedName,
                };
            },
            ct);

    public Task<Result<NovelShelfState>> ReorderShelvesAsync(
        IReadOnlyList<string> names,
        CancellationToken ct = default) =>
        MutateAsync(
            document =>
            {
                ArgumentNullException.ThrowIfNull(names);
                if (names.Count != document.Shelves.Count
                    || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
                {
                    throw new InvalidDataException("Shelf order must contain every shelf once.");
                }

                var reordered = names.Select(name =>
                {
                    var shelf = document.Shelves.FirstOrDefault(item => NamesEqual(item.Name, name));
                    return shelf ?? throw new InvalidDataException("Shelf order contains an unknown shelf.");
                }).ToList();
                document.Shelves.Clear();
                document.Shelves.AddRange(reordered);
            },
            ct);

    public Task<Result<NovelShelfState>> DeleteAsync(
        string name,
        CancellationToken ct = default) =>
        MutateAsync(
            document =>
            {
                var index = FindShelfIndex(document.Shelves, name);
                if (index < 0)
                    throw new InvalidDataException("Shelf not found.");
                var removed = document.Shelves[index];
                document.Shelves.RemoveAt(index);
                foreach (var bookId in removed.BookIds)
                {
                    if (!document.Unshelved.Contains(bookId, StringComparer.Ordinal))
                        document.Unshelved.Add(bookId);
                }
                ApplySubsetOrder(document.GlobalOrder, document.Unshelved);
            },
            ct);

    public Task<Result<NovelShelfState>> MoveBooksAsync(
        IReadOnlyList<string> bookIds,
        string? targetShelf,
        CancellationToken ct = default) =>
        MutateAsync(
            document =>
            {
                ArgumentNullException.ThrowIfNull(bookIds);
                var moving = bookIds
                    .Where(id => document.ValidBookIds.Contains(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (moving.Count == 0)
                    return;

                var movingSet = moving.ToHashSet(StringComparer.Ordinal);
                for (var index = 0; index < document.Shelves.Count; index++)
                {
                    document.Shelves[index] = document.Shelves[index] with
                    {
                        BookIds = document.Shelves[index].BookIds
                            .Where(id => !movingSet.Contains(id))
                            .ToList(),
                    };
                }
                document.Unshelved.RemoveAll(id => movingSet.Contains(id));

                if (targetShelf is null)
                {
                    document.Unshelved.AddRange(moving);
                    ApplySubsetOrder(document.GlobalOrder, document.Unshelved);
                }
                else
                {
                    var targetIndex = FindShelfIndex(document.Shelves, targetShelf);
                    if (targetIndex < 0)
                        throw new InvalidDataException("Shelf not found.");
                    document.Shelves[targetIndex] = document.Shelves[targetIndex] with
                    {
                        BookIds = document.Shelves[targetIndex].BookIds.Concat(moving).ToList(),
                    };
                }
            },
            ct);

    public Task<Result<NovelShelfState>> ReorderBookAsync(
        string sourceId,
        string targetId,
        string? shelf,
        CancellationToken ct = default) =>
        MutateAsync(
            document =>
            {
                if (shelf is null)
                {
                    ReorderBefore(document.Unshelved, sourceId, targetId);
                    ApplySubsetOrder(document.GlobalOrder, document.Unshelved);
                    return;
                }

                var index = FindShelfIndex(document.Shelves, shelf);
                if (index < 0)
                    throw new InvalidDataException("Shelf not found.");
                var order = document.Shelves[index].BookIds.ToList();
                ReorderBefore(order, sourceId, targetId);
                document.Shelves[index] = document.Shelves[index] with { BookIds = order };
            },
            ct);

    public Task<Result<NovelShelfState>> RemoveBookAsync(
        string bookId,
        CancellationToken ct = default) =>
        MutateAsync(
            document =>
            {
                for (var index = 0; index < document.Shelves.Count; index++)
                {
                    document.Shelves[index] = document.Shelves[index] with
                    {
                        BookIds = document.Shelves[index].BookIds
                            .Where(id => !string.Equals(id, bookId, StringComparison.Ordinal))
                            .ToList(),
                    };
                }
                document.Unshelved.RemoveAll(id => string.Equals(id, bookId, StringComparison.Ordinal));
                document.GlobalOrder.RemoveAll(id => string.Equals(id, bookId, StringComparison.Ordinal));
                document.ValidBookIds.Remove(bookId);
            },
            ct);

    private Task<Result<NovelShelfState>> MutateAsync(
        Action<ShelfDocument> mutation,
        CancellationToken ct) =>
        ExecuteAsync(
            async token =>
            {
                var document = await LoadCoreAsync(token);
                mutation(document);
                await SaveCoreAsync(document, token);
                return document.State;
            },
            requiresWrite: true,
            ct);

    private async Task<Result<NovelShelfState>> ExecuteAsync(
        Func<CancellationToken, Task<NovelShelfState>> action,
        bool requiresWrite,
        CancellationToken ct)
    {
        if (requiresWrite && _accessState.IsReadOnly)
        {
            return Result<NovelShelfState>.Failure(
                _accessState.ErrorMessage ?? "Novel storage migration requires recovery.",
                "Novel library is read-only");
        }

        await _gate.WaitAsync(ct);
        try
        {
            return Result<NovelShelfState>.Success(await action(ct));
        }
        catch (OperationCanceledException)
        {
            return Result<NovelShelfState>.Cancelled();
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or InvalidDataException
                                   or ArgumentException)
        {
            return Result<NovelShelfState>.Failure(ex.Message, "Shelf update failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ShelfDocument> LoadCoreAsync(CancellationToken ct)
    {
        var snapshot = await _storage.LoadSnapshotAsync(ct: ct);
        if (snapshot.CorruptMetadataPaths.Count > 0)
        {
            throw new InvalidDataException(
                "Shelf normalization is paused until corrupt book metadata is repaired.");
        }

        var validBookIds = snapshot.Books
            .Select(book => book.Id)
            .ToHashSet(StringComparer.Ordinal);
        var shelfResult = await _json.ReadAsync<List<NovelShelf>>(_shelvesPath, ct);
        if (shelfResult.Status == NovelJsonReadStatus.Invalid)
            throw new InvalidDataException(shelfResult.Error ?? "Invalid shelves.json.");
        var sourceShelves = shelfResult.Status == NovelJsonReadStatus.Missing
            ? []
            : shelfResult.Value ?? throw new InvalidDataException("Invalid shelves.json.");

        var shelves = NormalizeShelves(sourceShelves, validBookIds);
        var globalOrder = (await _storage.LoadBookOrderAsync(ct))
            .Where(validBookIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var bookId in validBookIds.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!globalOrder.Contains(bookId, StringComparer.Ordinal))
                globalOrder.Add(bookId);
        }

        var shelved = shelves
            .SelectMany(shelf => shelf.BookIds)
            .ToHashSet(StringComparer.Ordinal);
        var unshelved = globalOrder.Where(id => !shelved.Contains(id)).ToList();
        return new ShelfDocument(shelves, unshelved, globalOrder, validBookIds);
    }

    private async Task SaveCoreAsync(ShelfDocument document, CancellationToken ct)
    {
        await _json.WriteAsync(_shelvesPath, document.Shelves, ct);
        await _storage.SaveBookOrderAsync(document.GlobalOrder, ct);
    }

    private static List<NovelShelf> NormalizeShelves(
        IReadOnlyList<NovelShelf> source,
        HashSet<string> validBookIds)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var shelves = new List<NovelShelf>(source.Count);
        foreach (var shelf in source)
        {
            var name = NormalizeName(shelf.Name);
            if (!names.Add(name))
                throw new InvalidDataException("Shelf names must be unique.");
            var bookIds = (shelf.BookIds ?? [])
                .Where(id => validBookIds.Contains(id) && assigned.Add(id))
                .ToList();
            shelves.Add(new NovelShelf(name, bookIds));
        }
        return shelves;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Shelf name cannot be empty.");
        return name.Trim();
    }

    private static int FindShelfIndex(IReadOnlyList<NovelShelf> shelves, string name) =>
        Enumerable.Range(0, shelves.Count)
            .FirstOrDefault(index => NamesEqual(shelves[index].Name, name), -1);

    private static bool NamesEqual(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static void ReorderBefore(List<string> order, string sourceId, string targetId)
    {
        var sourceIndex = order.FindIndex(id => string.Equals(id, sourceId, StringComparison.Ordinal));
        var targetIndex = order.FindIndex(id => string.Equals(id, targetId, StringComparison.Ordinal));
        if (sourceIndex < 0 || targetIndex < 0)
            throw new InvalidDataException("Books must belong to the same section.");
        if (sourceIndex == targetIndex)
            return;
        var source = order[sourceIndex];
        order.RemoveAt(sourceIndex);
        targetIndex = order.FindIndex(id => string.Equals(id, targetId, StringComparison.Ordinal));
        order.Insert(targetIndex, source);
    }

    private static void ApplySubsetOrder(List<string> globalOrder, IReadOnlyList<string> subset)
    {
        var subsetIds = subset.ToHashSet(StringComparer.Ordinal);
        var replacementIndex = 0;
        for (var index = 0; index < globalOrder.Count; index++)
        {
            if (subsetIds.Contains(globalOrder[index]))
                globalOrder[index] = subset[replacementIndex++];
        }
    }

    private sealed class ShelfDocument(
        List<NovelShelf> shelves,
        List<string> unshelved,
        List<string> globalOrder,
        HashSet<string> validBookIds)
    {
        public List<NovelShelf> Shelves { get; } = shelves;
        public List<string> Unshelved { get; } = unshelved;
        public List<string> GlobalOrder { get; } = globalOrder;
        public HashSet<string> ValidBookIds { get; } = validBookIds;
        public NovelShelfState State => new(Shelves, Unshelved);
    }
}
