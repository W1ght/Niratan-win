using System;

namespace Hoshi.Models.Sync;

public sealed record GoogleDriveCredentials(
    string AccessToken,
    string RefreshToken,
    string ClientId,
    DateTimeOffset ExpiresAtUtc,
    string Scope)
{
    public bool ShouldRefresh(DateTimeOffset now) =>
        ExpiresAtUtc <= now.AddMinutes(2);
}
