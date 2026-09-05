using System;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Services.Settings;

namespace Niratan.Services.Video;

internal sealed class AniDbConfigurationProvider : IAniDbConfigurationProvider
{
    private readonly ISettingsService _settings;
    private readonly IVideoMetadataCredentialStore _credentials;

    public AniDbConfigurationProvider(
        ISettingsService settings,
        IVideoMetadataCredentialStore credentials)
    {
        _settings = settings;
        _credentials = credentials;
    }

    public async Task<AniDbClientConfiguration?> GetAsync(CancellationToken ct = default)
    {
        var metadata = _settings.Current.VideoSettings.Metadata;
        if (!metadata.OnlineConsentAccepted || !metadata.AniDbEnabled
            || string.IsNullOrWhiteSpace(metadata.AniDbClientId)
            || metadata.AniDbClientVersion <= 0)
            return null;

        var username = await _credentials.ReadAsync("anidb", "username", ct);
        var password = await _credentials.ReadAsync("anidb", "password", ct);
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        return new AniDbClientConfiguration(
            metadata.AniDbClientId.Trim(),
            metadata.AniDbClientVersion,
            username.Trim(),
            password,
            Math.Clamp(metadata.AniDbUdpLocalPort, 1024, 65535),
            metadata.AniDbHashMatchingEnabled,
            metadata.AniDbMyListSyncEnabled,
            metadata.AniDbAutoAddToMyList,
            Enum.IsDefined(typeof(AniDbMyListState), metadata.AniDbDefaultMyListState)
                ? (AniDbMyListState)metadata.AniDbDefaultMyListState
                : AniDbMyListState.OnHdd,
            Math.Clamp(metadata.AniDbRelationDepth, 0, 5))
        {
            HttpClientId = string.IsNullOrWhiteSpace(metadata.AniDbHttpClientId)
                ? null
                : metadata.AniDbHttpClientId.Trim(),
            HttpClientVersion = !string.IsNullOrWhiteSpace(metadata.AniDbHttpClientId)
                                && metadata.AniDbHttpClientVersion > 0
                ? metadata.AniDbHttpClientVersion
                : null,
            UdpServerHost = string.IsNullOrWhiteSpace(metadata.AniDbUdpServerHost)
                ? "api.anidb.net"
                : metadata.AniDbUdpServerHost.Trim(),
            UdpServerPort = Math.Clamp(metadata.AniDbUdpServerPort, 1, 65535),
            UdpBindAddress = string.IsNullOrWhiteSpace(metadata.AniDbUdpBindAddress)
                ? null
                : metadata.AniDbUdpBindAddress.Trim(),
            MyListReadWatched = metadata.AniDbMyListReadWatched,
            MyListReadUnwatched = metadata.AniDbMyListReadUnwatched,
            MyListSetWatched = metadata.AniDbMyListSetWatched,
            MyListSetUnwatched = metadata.AniDbMyListSetUnwatched,
        };
    }
}
