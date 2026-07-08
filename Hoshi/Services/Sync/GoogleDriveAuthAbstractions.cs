using System;
using System.Threading;
using System.Threading.Tasks;
using Hoshi.Models.Sync;

namespace Hoshi.Services.Sync;

public interface IGoogleDriveAuthService
{
    bool HasCredentials { get; }

    Task AuthenticateAsync(string clientId, CancellationToken ct = default);

    Task<string> GetAccessTokenAsync(CancellationToken ct = default);

    Task SignOutAsync(CancellationToken ct = default);
}

public interface IGoogleDriveCredentialStore
{
    bool HasCredentials { get; }

    Task<GoogleDriveCredentials?> LoadAsync(CancellationToken ct = default);

    Task SaveAsync(GoogleDriveCredentials credentials, CancellationToken ct = default);

    Task DeleteAsync(CancellationToken ct = default);
}

public interface IGoogleOAuthLoopbackReceiver
{
    Task<GoogleOAuthLoopbackSession> StartAsync(string state, CancellationToken ct = default);
}

public interface IBrowserLauncher
{
    Task LaunchAsync(Uri uri, CancellationToken ct = default);
}

public sealed record GoogleOAuthCallback(
    string? Code,
    string? State,
    string? Error);

public sealed class GoogleOAuthLoopbackSession : IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;

    public GoogleOAuthLoopbackSession(
        Uri redirectUri,
        Task<GoogleOAuthCallback> callbackTask,
        Func<ValueTask> dispose)
    {
        RedirectUri = redirectUri;
        CallbackTask = callbackTask;
        _dispose = dispose;
    }

    public Uri RedirectUri { get; }

    public Task<GoogleOAuthCallback> CallbackTask { get; }

    public ValueTask DisposeAsync() => _dispose();
}
