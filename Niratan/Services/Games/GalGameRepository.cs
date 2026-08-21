using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Games;
using Niratan.Services.Novels;

namespace Niratan.Services.Games;

internal sealed class GalGameRepository : IGalGameRepository
{
    private readonly INiratanJsonFileStore _store;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(
        AppDataHelper.GetGameDataPath(),
        "galgame-library.json");

    private GalGameLibraryDocument? _document;
    private string? _loadError;

    public GalGameRepository(INiratanJsonFileStore store) => _store = store;

    public async Task<GalGameRepositoryReadResult> LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            return new GalGameRepositoryReadResult(
                _document!.Games.ToArray(),
                _loadError);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task AddAsync(GalGameEntry entry, CancellationToken ct = default) =>
        MutateAsync(document =>
        {
            if (document.Games.Any(game => string.Equals(game.Id, entry.Id, StringComparison.Ordinal)))
                throw new InvalidOperationException("A game with this id already exists.");
            if (GalGameLibraryFunctions.FindByExePath(document.Games, entry.ExePath) is not null)
                throw new InvalidOperationException("This executable is already in the game library.");
            document.Games.Add(entry);
        }, ct);

    public Task UpdateAsync(GalGameEntry entry, CancellationToken ct = default) =>
        MutateAsync(document =>
        {
            var index = document.Games.FindIndex(game => game.Id == entry.Id);
            if (index < 0)
                throw new KeyNotFoundException($"Game '{entry.Id}' was not found.");
            document.Games[index] = entry;
        }, ct);

    public Task RemoveAsync(string id, CancellationToken ct = default) =>
        MutateAsync(document =>
        {
            var index = document.Games.FindIndex(game => game.Id == id);
            if (index >= 0)
                document.Games.RemoveAt(index);
        }, ct);

    private async Task MutateAsync(Action<GalGameLibraryDocument> mutation, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureLoadedAsync(ct);
            if (_loadError is not null)
                throw new InvalidDataException(_loadError);
            mutation(_document!);
            await _store.WriteAsync(_path, _document, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_document is not null || _loadError is not null)
            return;

        var result = await _store.ReadAsync<GalGameLibraryDocument>(_path, ct);
        if (result.Status == NovelJsonReadStatus.Missing)
        {
            _document = new GalGameLibraryDocument();
            return;
        }

        if (result.Status != NovelJsonReadStatus.Success || result.Value is null)
        {
            _loadError = result.Error ?? "The game library file is invalid.";
            _document = new GalGameLibraryDocument();
            return;
        }

        _document = result.Value with
        {
            Games = result.Value.Games ?? [],
        };
    }
}
