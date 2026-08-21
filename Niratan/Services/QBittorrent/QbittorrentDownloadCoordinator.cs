using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;
using Niratan.Services.Settings;

namespace Niratan.Services.QBittorrent;

public sealed class QbittorrentDownloadCoordinator : IQbittorrentDownloadCoordinator
{
    private readonly ISettingsService _settingsService;
    private readonly IQbittorrentCredentialStore _credentialStore;
    private readonly IQbittorrentClient _client;
    private readonly ILogger<QbittorrentDownloadCoordinator> _logger;
    private readonly object _gate = new();
    private IReadOnlyList<QbittorrentTorrent> _tasks = [];

    public event EventHandler? TasksChanged;

    public QbittorrentDownloadCoordinator(
        ISettingsService settingsService,
        IQbittorrentCredentialStore credentialStore,
        IQbittorrentClient client,
        ILogger<QbittorrentDownloadCoordinator> logger)
    {
        _settingsService = settingsService;
        _credentialStore = credentialStore;
        _client = client;
        _logger = logger;
    }

    public IReadOnlyList<QbittorrentTorrent> GetTasks()
    {
        lock (_gate)
            return _tasks.ToList();
    }

    public async Task<Result<IReadOnlyList<QbittorrentTorrent>>> RefreshAsync(
        CancellationToken ct = default)
    {
        var credentials = await _credentialStore.LoadAsync(ct);
        if (credentials is null)
            return Result<IReadOnlyList<QbittorrentTorrent>>.Failure(
                "Configure qBittorrent credentials first.",
                "qBittorrent is not configured");

        var result = await _client.GetTorrentsAsync(
            _settingsService.Current.QbittorrentSettings,
            credentials,
            ct);
        if (result.IsSuccess)
        {
            lock (_gate)
                _tasks = result.Value ?? [];
            TasksChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (!result.IsCancelled)
        {
            _logger.LogDebug("qBittorrent refresh failed: {Error}", result.Error);
        }

        return result;
    }

    public async Task<Result<QbittorrentTorrentDetails>> GetDetailsAsync(
        string hash,
        CancellationToken ct = default)
    {
        var credentials = await _credentialStore.LoadAsync(ct);
        if (credentials is null)
            return Result<QbittorrentTorrentDetails>.Failure(
                "Configure qBittorrent credentials first.",
                "qBittorrent is not configured");

        return await _client.GetTorrentDetailsAsync(
            _settingsService.Current.QbittorrentSettings,
            credentials,
            hash,
            ct);
    }

    public async Task<Result> AddAsync(NyaaTorrentItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var credentials = await _credentialStore.LoadAsync(ct);
        if (credentials is null)
            return Result.Failure("Configure qBittorrent credentials first.", "qBittorrent is not configured");

        var result = await _client.AddTorrentAsync(
            _settingsService.Current.QbittorrentSettings,
            credentials,
            item,
            ct);
        if (result.IsSuccess)
            await RefreshAsync(ct);
        return result;
    }

    public Task<Result> PauseAsync(string hash, CancellationToken ct = default) =>
        SendTaskActionAsync((settings, credentials) => _client.PauseAsync(settings, credentials, hash, ct), ct);

    public Task<Result> ResumeAsync(string hash, CancellationToken ct = default) =>
        SendTaskActionAsync((settings, credentials) => _client.ResumeAsync(settings, credentials, hash, ct), ct);

    public Task<Result> DeleteAsync(
        string hash,
        bool deleteFiles,
        CancellationToken ct = default) =>
        SendTaskActionAsync(
            (settings, credentials) => _client.DeleteAsync(settings, credentials, hash, deleteFiles, ct),
            ct);

    private async Task<Result> SendTaskActionAsync(
        Func<QbittorrentSettings, QbittorrentCredentials, Task<Result>> action,
        CancellationToken ct)
    {
        var credentials = await _credentialStore.LoadAsync(ct);
        if (credentials is null)
            return Result.Failure("Configure qBittorrent credentials first.", "qBittorrent is not configured");

        var result = await action(_settingsService.Current.QbittorrentSettings, credentials);
        if (result.IsSuccess)
            await RefreshAsync(ct);
        return result;
    }
}
