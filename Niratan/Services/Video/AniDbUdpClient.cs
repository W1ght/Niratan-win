using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Niratan.Services.Video;

internal sealed class AniDbUdpSocketTransport : IAniDbUdpTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UdpClient? _client;
    private int _localPort;
    private IPAddress? _bindAddress;

    public async Task<string> SendAsync(
        string host,
        int serverPort,
        int localPort,
        string? bindAddress,
        string command,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var localAddress = ParseBindAddress(bindAddress);
            if (_client == null || _localPort != localPort || !Equals(_bindAddress, localAddress))
            {
                _client?.Dispose();
                _client = new UdpClient(new IPEndPoint(localAddress, localPort));
                _localPort = localPort;
                _bindAddress = localAddress;
            }

            var addresses = await Dns.GetHostAddressesAsync(host, ct);
            var address = addresses.FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork)
                          ?? throw new SocketException((int)SocketError.HostNotFound);
            var remoteEndPoint = new IPEndPoint(address, serverPort);
            var payload = Encoding.UTF8.GetBytes(command);
            await _client.SendAsync(payload, remoteEndPoint, ct);
            UdpReceiveResult response;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    do
                    {
                        response = await _client.ReceiveAsync(timeout.Token);
                    }
                    while (!response.RemoteEndPoint.Equals(remoteEndPoint));
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _client.Dispose();
                    _client = null;
                    throw new TimeoutException("AniDB UDP request timed out.");
                }
            }
            var bytes = response.Buffer;
            if (bytes.Length > 2 && bytes[0] == 0 && bytes[1] == 0)
            {
                await using var compressed = new MemoryStream(bytes, 2, bytes.Length - 2, writable: false);
                await using var inflater = new ZLibStream(compressed, CompressionMode.Decompress);
                await using var output = new MemoryStream();
                await inflater.CopyToAsync(output, ct);
                if (output.Length > 64 * 1024)
                    throw new InvalidDataException("AniDB UDP response exceeded 64 KiB.");
                bytes = output.ToArray();
            }
            return Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static IPAddress ParseBindAddress(string? bindAddress)
    {
        if (string.IsNullOrWhiteSpace(bindAddress))
            return IPAddress.Any;
        if (!IPAddress.TryParse(bindAddress.Trim(), out var address)
            || address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException(
                "AniDB UDP bind address must be an IPv4 address.",
                nameof(bindAddress));
        }

        return address;
    }
}

internal sealed class AniDbUdpClient : IAniDbUdpClient
{
    private static readonly TimeSpan DefaultShortRequestInterval = TimeSpan.FromMilliseconds(2_100);
    private static readonly TimeSpan DefaultLongRequestInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DefaultSustainedActivityPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultIdleResetPeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultBackoffPeriod = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultBanPeriod = TimeSpan.FromMinutes(90);
    private readonly IAniDbUdpTransport _transport;
    private readonly IAniDbConfigurationProvider _configuration;
    private readonly ILogger<AniDbUdpClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _shortRequestInterval;
    private readonly TimeSpan _longRequestInterval;
    private readonly TimeSpan _sustainedActivityPeriod;
    private readonly TimeSpan _idleResetPeriod;
    private readonly TimeSpan _backoffPeriod;
    private readonly TimeSpan _banPeriod;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private string? _session;
    private DateTimeOffset? _lastRequestAt;
    private DateTimeOffset? _activityStartedAt;
    private DateTimeOffset? _banUntil;
    private DateTimeOffset? _backoffUntil;

    public AniDbUdpClient(
        IAniDbUdpTransport transport,
        IAniDbConfigurationProvider configuration,
        ILogger<AniDbUdpClient> logger)
        : this(
            transport,
            configuration,
            logger,
            DefaultShortRequestInterval,
            DefaultLongRequestInterval,
            DefaultSustainedActivityPeriod,
            DefaultIdleResetPeriod,
            DefaultBackoffPeriod,
            DefaultBanPeriod,
            static () => DateTimeOffset.UtcNow,
            static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal AniDbUdpClient(
        IAniDbUdpTransport transport,
        IAniDbConfigurationProvider configuration,
        ILogger<AniDbUdpClient> logger,
        TimeSpan requestInterval)
        : this(
            transport,
            configuration,
            logger,
            requestInterval,
            requestInterval,
            TimeSpan.MaxValue,
            TimeSpan.MaxValue,
            DefaultBackoffPeriod,
            DefaultBanPeriod,
            static () => DateTimeOffset.UtcNow,
            static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal AniDbUdpClient(
        IAniDbUdpTransport transport,
        IAniDbConfigurationProvider configuration,
        ILogger<AniDbUdpClient> logger,
        TimeSpan shortRequestInterval,
        TimeSpan longRequestInterval,
        TimeSpan sustainedActivityPeriod,
        TimeSpan idleResetPeriod,
        TimeSpan backoffPeriod,
        TimeSpan banPeriod,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shortRequestInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(longRequestInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(sustainedActivityPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(idleResetPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(backoffPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(banPeriod, TimeSpan.Zero);
        _transport = transport;
        _configuration = configuration;
        _logger = logger;
        _shortRequestInterval = shortRequestInterval;
        _longRequestInterval = longRequestInterval;
        _sustainedActivityPeriod = sustainedActivityPeriod;
        _idleResetPeriod = idleResetPeriod;
        _backoffPeriod = backoffPeriod;
        _banPeriod = banPeriod;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public event EventHandler<AniDbClientStatus>? StatusChanged;

    public AniDbClientStatus Status { get; private set; } = new(
        AniDbClientConnectionState.Ready,
        null,
        DateTimeOffset.UtcNow);

    public async Task<bool> TestLoginAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _session = null;
            var configuration = await RequireConfigurationAsync(ct);
            return await LoginCoreAsync(configuration, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<AniDbFileMatch?> GetFileAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) =>
        SendAuthenticatedAsync(
            $"FILE size={fileSize}&ed2k={EscapeHash(ed2k)}&fmask=7700C0D900&amask=000000C0",
            response => response.Code == 320 ? null : ParseFile(response),
            ct);

    public Task<AniDbAnime?> GetAnimeMetadataAsync(
        int animeId,
        CancellationToken ct = default)
    {
        if (animeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(animeId));
        return SendAuthenticatedAsync(
            $"ANIME aid={animeId.ToString(CultureInfo.InvariantCulture)}&amask=FCFCFEFF7F00F8",
            response => response.Code == 330 ? null : ParseAnimeMetadata(response),
            ct);
    }

    public Task<AniDbEpisode?> GetEpisodeMetadataAsync(
        int episodeId,
        CancellationToken ct = default)
    {
        if (episodeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(episodeId));
        return SendAuthenticatedAsync(
            $"EPISODE eid={episodeId.ToString(CultureInfo.InvariantCulture)}",
            response => response.Code == 340 ? null : ParseEpisodeMetadata(response),
            ct);
    }

    public async Task<AniDbEpisodeIdentity?> GetEpisodeIdentityAsync(
        int episodeId,
        CancellationToken ct = default)
    {
        var episode = await GetEpisodeMetadataAsync(episodeId, ct);
        return episode == null
            ? null
            : new AniDbEpisodeIdentity(episode.EpisodeId, episode.AnimeId);
    }

    public Task<AniDbMyListEntry?> GetMyListAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default) =>
        SendAuthenticatedAsync(
            $"MYLIST size={fileSize}&ed2k={EscapeHash(ed2k)}",
            response => response.Code == 321 ? null : ParseMyList(response),
            ct);

    public async Task<AniDbMyListEntry?> AddOrUpdateMyListAsync(
        string ed2k,
        long fileSize,
        AniDbMyListState state,
        bool watched,
        DateTimeOffset? watchedAt,
        CancellationToken ct = default)
    {
        var existing = await GetMyListAsync(ed2k, fileSize, ct);
        var command = $"MYLISTADD size={fileSize}&ed2k={EscapeHash(ed2k)}&state={(int)state}";
        if (existing != null)
            command += "&edit=1";
        command += watched
            ? $"&viewed=1&viewdate={(watchedAt ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds()}"
            : "&viewed=0";
        return await SendAuthenticatedAsync(
            command,
            response => ParseMyListMutation(response, state, watched, watchedAt),
            ct);
    }

    public async Task DeleteMyListAsync(
        string ed2k,
        long fileSize,
        CancellationToken ct = default)
    {
        await SendAuthenticatedAsync<object?>(
            $"MYLISTDEL size={fileSize}&ed2k={EscapeHash(ed2k)}",
            response => response.Code is 211 or 411 ? null
                : throw Unexpected(response, "MYLISTDEL"),
            ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (string.IsNullOrWhiteSpace(_session))
                return;
            var configuration = await RequireConfigurationAsync(ct);
            var response = await SendRawCoreAsync(
                configuration,
                $"LOGOUT s={Uri.EscapeDataString(_session)}",
                ct);
            HandleGlobalStatus(response);
            _session = null;
            Publish(AniDbClientConnectionState.Ready, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T> SendAuthenticatedAsync<T>(
        string command,
        Func<UdpResponse, T> parser,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var configuration = await RequireConfigurationAsync(ct);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (string.IsNullOrWhiteSpace(_session)
                    && !await LoginCoreAsync(configuration, ct))
                    throw new InvalidOperationException("AniDB login failed.");
                var separator = command.Contains(' ') ? '&' : ' ';
                var response = await SendRawCoreAsync(
                    configuration,
                    command + separator + "s=" + Uri.EscapeDataString(_session!),
                    ct);
                if (response.Code is 501 or 506 or 598)
                {
                    _session = null;
                    continue;
                }
                HandleGlobalStatus(response);
                return parser(response);
            }
            throw new InvalidOperationException("AniDB session could not be renewed.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AniDbClientConfiguration> RequireConfigurationAsync(CancellationToken ct)
    {
        var now = _utcNow();
        if (_banUntil is { } banUntil && banUntil > now)
        {
            Publish(AniDbClientConnectionState.Banned, "AniDB temporarily banned this client.", banUntil);
            throw new InvalidOperationException($"AniDB requests are paused until {banUntil:u}.");
        }
        if (_banUntil is not null)
        {
            _banUntil = null;
            Publish(AniDbClientConnectionState.Ready, null);
        }
        if (_backoffUntil is { } backoffUntil && backoffUntil > now)
        {
            Publish(AniDbClientConnectionState.BackingOff,
                "AniDB is temporarily unavailable.", backoffUntil);
            throw new InvalidOperationException($"AniDB requests are paused until {backoffUntil:u}.");
        }
        if (_backoffUntil is not null)
        {
            _backoffUntil = null;
            Publish(AniDbClientConnectionState.Ready, null);
        }
        var configuration = await _configuration.GetAsync(ct);
        if (configuration == null)
        {
            Publish(AniDbClientConnectionState.MissingConfiguration,
                "AniDB username, password, registered client ID, or client version is missing.");
            throw new InvalidOperationException("AniDB client configuration is incomplete.");
        }
        return configuration;
    }

    private async Task<bool> LoginCoreAsync(
        AniDbClientConfiguration configuration,
        CancellationToken ct)
    {
        Publish(AniDbClientConnectionState.Authenticating, null);
        var command = "AUTH"
                      + " user=" + Uri.EscapeDataString(configuration.Username)
                      + "&pass=" + Uri.EscapeDataString(configuration.Password)
                      + "&protover=3"
                      + "&client=" + Uri.EscapeDataString(configuration.ClientId.ToLowerInvariant())
                      + "&clientver=" + configuration.ClientVersion.ToString(CultureInfo.InvariantCulture)
                      + "&comp=1&enc=UTF-8";
        var response = await SendRawCoreAsync(configuration, command, ct);
        HandleGlobalStatus(response);
        if (response.Code is not (200 or 201))
        {
            if (response.Code is 500 or 502)
                Publish(AniDbClientConnectionState.LoginFailed, response.Message);
            return false;
        }

        _session = response.Message
            .Split([' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(_session))
            throw Unexpected(response, "AUTH");
        Publish(AniDbClientConnectionState.Connected, null);
        return true;
    }

    private async Task<UdpResponse> SendRawCoreAsync(
        AniDbClientConfiguration configuration,
        string command,
        CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await EnforceRequestRateAsync(ct);
            try
            {
                var raw = await _transport.SendAsync(
                    configuration.UdpServerHost,
                    configuration.UdpServerPort,
                    configuration.UdpLocalPort,
                    configuration.UdpBindAddress,
                    command,
                    ct);
                _lastRequestAt = _utcNow();
                return ParseEnvelope(raw);
            }
            catch (Exception ex) when (ex is SocketException or TimeoutException or IOException)
            {
                _lastRequestAt = _utcNow();
                if (attempt == 0)
                {
                    // A missing UDP datagram is not an AniDB overload response. Shoko
                    // retries it once through the same rate limiter; only explicit
                    // 600/601/602/604 responses open the provider-wide backoff gate.
                    _logger.LogWarning(ex, "AniDB UDP request failed; retrying once");
                    continue;
                }

                _logger.LogWarning(ex, "AniDB UDP request failed after one retry");
                throw;
            }
        }
    }

    private async Task EnforceRequestRateAsync(CancellationToken ct)
    {
        var now = _utcNow();
        if (_lastRequestAt is not { } lastRequestAt)
        {
            _activityStartedAt = now;
            return;
        }

        var idle = now - lastRequestAt;
        if (idle >= _idleResetPeriod)
        {
            _activityStartedAt = now;
            return;
        }

        _activityStartedAt ??= lastRequestAt;
        var interval = now - _activityStartedAt.Value >= _sustainedActivityPeriod
            ? _longRequestInterval
            : _shortRequestInterval;
        var delay = interval - idle;
        if (delay > TimeSpan.Zero)
            await _delayAsync(delay, ct);
    }

    private void HandleGlobalStatus(UdpResponse response)
    {
        if (response.Code == 555)
        {
            _session = null;
            _backoffUntil = null;
            _banUntil = _utcNow().Add(_banPeriod);
            Publish(AniDbClientConnectionState.Banned, response.Message, _banUntil);
            throw new InvalidOperationException("AniDB temporarily banned this client.");
        }
        if (response.Code is 600 or 601 or 602 or 604)
        {
            BeginBackoff(response.Message);
            throw new InvalidOperationException($"AniDB is temporarily unavailable ({response.Code}).");
        }
    }

    private void BeginBackoff(string message)
    {
        _backoffUntil = _utcNow().Add(_backoffPeriod);
        Publish(AniDbClientConnectionState.BackingOff, message, _backoffUntil);
    }

    private static UdpResponse ParseEnvelope(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidDataException("AniDB returned an empty UDP response.");
        var firstBreak = raw.IndexOf('\n');
        var header = (firstBreak >= 0 ? raw[..firstBreak] : raw).Trim();
        var body = firstBreak >= 0 ? raw[(firstBreak + 1)..].TrimEnd('\r', '\n') : string.Empty;
        var parts = header.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var code))
            throw new InvalidDataException("AniDB returned a malformed UDP status line.");
        var message = parts.Length > 1 ? parts[1] : string.Empty;
        if (body.Length > 0)
            message = message.Length > 0 ? message + "\n" + body : body;
        return new UdpResponse(code, message, body);
    }

    private static AniDbFileMatch ParseFile(UdpResponse response)
    {
        if (response.Code != 220)
            throw Unexpected(response, "FILE");
        var parts = response.Body.Split('|').Select(value => value.Trim()).ToArray();
        if (parts.Length != 16
            || !int.TryParse(parts[0], out var fileId)
            || !int.TryParse(parts[1], out var animeId))
            throw Unexpected(response, "FILE");
        var episodes = ParseEpisodeLinks(parts[2], false)
            .Select(link => link with { AnimeId = animeId })
            .Concat(ParseEpisodeLinks(parts[4], true)
                .Select(link => link with { AnimeId = 0 }))
            .Select((link, index) => link with { Ordinal = index })
            .ToImmutableArray();
        var state = int.TryParse(parts[6], out var stateValue) ? stateValue : 0;
        var version = (state & 0x20) != 0 ? 5
            : (state & 0x10) != 0 ? 4
            : (state & 0x08) != 0 ? 3
            : (state & 0x04) != 0 ? 2
            : 1;
        DateOnly? releasedAt = long.TryParse(parts[12], out var epoch) && epoch > 0
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime)
            : null;
        return new AniDbFileMatch(
            fileId,
            animeId,
            int.TryParse(parts[3], out var groupId) && groupId > 0 ? groupId : null,
            EmptyToNull(parts[14]),
            EmptyToNull(parts[15]),
            parts[5] == "1",
            version,
            (state & 0x40) != 0 ? false : (state & 0x80) != 0 ? true : null,
            (state & 0x01) != 0 ? true : (state & 0x02) != 0 ? false : null,
            (state & 0x1000) != 0,
            EmptyToNull(parts[7]),
            EmptyToNull(parts[8]),
            SplitApostrophe(parts[9]),
            SplitApostrophe(parts[10]),
            EmptyToNull(parts[11]),
            EmptyToNull(parts[13]),
            releasedAt,
            episodes);
    }

    private static AniDbAnime ParseAnimeMetadata(UdpResponse response)
    {
        if (response.Code != 230)
            throw Unexpected(response, "ANIME");
        var parts = response.Body.Split('|').Select(value => value.Trim()).ToArray();
        if (parts.Length < 34
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var animeId)
            || animeId <= 0)
            throw Unexpected(response, "ANIME");

        var titles = ImmutableArray.CreateBuilder<AniDbTitle>();
        AddTitle(titles, "x-jat", "main", parts.ElementAtOrDefault(6));
        AddTitle(titles, "ja", "official", parts.ElementAtOrDefault(7));
        AddTitle(titles, "en", "official", parts.ElementAtOrDefault(8));
        foreach (var alternateTitle in SplitApostrophe(parts.ElementAtOrDefault(9) ?? string.Empty))
            AddTitle(titles, "", "official", alternateTitle);
        foreach (var shortTitle in SplitApostrophe(parts.ElementAtOrDefault(10) ?? string.Empty))
            AddTitle(titles, "", "short", shortTitle);
        foreach (var synonymTitle in SplitApostrophe(parts.ElementAtOrDefault(11) ?? string.Empty))
            AddTitle(titles, "", "synonym", synonymTitle);

        var relationIds = SplitApostrophe(parts.ElementAtOrDefault(4) ?? string.Empty);
        var relationTypes = SplitApostrophe(parts.ElementAtOrDefault(5) ?? string.Empty);
        var relations = ImmutableArray.CreateBuilder<AniDbRelation>();
        for (var index = 0; index < Math.Min(relationIds.Length, relationTypes.Length); index++)
        {
            if (!int.TryParse(relationIds[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var relatedId)
                || !int.TryParse(relationTypes[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawType)
                || relatedId <= 0)
                continue;
            relations.Add(new AniDbRelation(
                animeId,
                relatedId,
                MapRelationType(rawType),
                null)
            {
                Verified = true,
            });
        }

        var tagNames = SplitApostrophe(parts.ElementAtOrDefault(30) ?? string.Empty);
        var tagIds = SplitApostrophe(parts.ElementAtOrDefault(31) ?? string.Empty);
        var tagWeights = SplitApostrophe(parts.ElementAtOrDefault(32) ?? string.Empty);
        var tags = ImmutableArray.CreateBuilder<AniDbTag>();
        for (var index = 0; index < Math.Min(tagNames.Length, Math.Min(tagIds.Length, tagWeights.Length)); index++)
        {
            if (!int.TryParse(tagIds[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tagId)
                || !int.TryParse(tagWeights[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var weight)
                || tagId <= 0)
                continue;
            tags.Add(new AniDbTag(tagId, null, tagNames[index], null, weight, false, false)
            {
                Verified = true,
                UpdatedAt = ParseEpoch(parts.ElementAtOrDefault(33)),
            });
        }

        var resources = ImmutableArray.CreateBuilder<AniDbResource>();
        AddResource(resources, 1, parts.ElementAtOrDefault(27));
        AddResource(resources, 9, parts.ElementAtOrDefault(28));
        var now = DateTimeOffset.UtcNow;
        var title = EmptyToNull(parts.ElementAtOrDefault(6) ?? string.Empty)
                    ?? EmptyToNull(parts.ElementAtOrDefault(8) ?? string.Empty)
                    ?? EmptyToNull(parts.ElementAtOrDefault(7) ?? string.Empty)
                    ?? $"AniDB {animeId.ToString(CultureInfo.InvariantCulture)}";
        return new AniDbAnime(
            animeId,
            EmptyToNull(parts.ElementAtOrDefault(3) ?? string.Empty) ?? "Anime",
            title,
            EmptyToNull(parts.ElementAtOrDefault(7) ?? string.Empty),
            null,
            FormatEpochDate(parts.ElementAtOrDefault(15)),
            FormatEpochDate(parts.ElementAtOrDefault(16)),
            EmptyToNull(parts.ElementAtOrDefault(18) ?? string.Empty),
            ParseInt(parts.ElementAtOrDefault(12)) ?? 0,
            parts.ElementAtOrDefault(26) == "1",
            ParseAniDbRating(parts.ElementAtOrDefault(19)),
            titles.ToImmutable(),
            [],
            relations.ToImmutable(),
            tags.ToImmutable(),
            [],
            now,
            now.AddDays(7))
        {
            IsDegraded = true,
            Url = EmptyToNull(parts.ElementAtOrDefault(17) ?? string.Empty),
            Resources = resources.ToImmutable(),
        };
    }

    private static AniDbEpisode ParseEpisodeMetadata(UdpResponse response)
    {
        if (response.Code != 240)
            throw Unexpected(response, "EPISODE");
        var parts = response.Body.Split('|').Select(value => value.Trim()).ToArray();
        if (parts.Length < 6
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var episodeId)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var animeId)
            || episodeId <= 0
            || animeId <= 0)
            throw Unexpected(response, "EPISODE");
        var rawNumber = parts[5];
        var type = (ParseInt(parts.ElementAtOrDefault(10)), rawNumber.FirstOrDefault()) switch
        {
            (2, _) or (_, 'S') => AniDbEpisodeType.Special,
            (3, _) or (_, 'C') => AniDbEpisodeType.Credits,
            (4, _) or (_, 'T') => AniDbEpisodeType.Trailer,
            (5, _) or (_, 'P') => AniDbEpisodeType.Parody,
            (6, _) or (_, 'O') => AniDbEpisodeType.Other,
            _ => AniDbEpisodeType.Regular,
        };
        var digits = new string(rawNumber
            .SkipWhile(character => !char.IsDigit(character))
            .TakeWhile(char.IsDigit)
            .ToArray());
        var titles = ImmutableArray.CreateBuilder<AniDbTitle>();
        AddTitle(titles, "en", "episode", parts.ElementAtOrDefault(6));
        AddTitle(titles, "x-jat", "episode", parts.ElementAtOrDefault(7));
        AddTitle(titles, "ja", "episode", parts.ElementAtOrDefault(8));
        return new AniDbEpisode(
            episodeId,
            animeId,
            type,
            ParseInt(digits) ?? 0,
            rawNumber,
            ParseInt(parts.ElementAtOrDefault(2)) ?? 0,
            FormatEpochDate(parts.ElementAtOrDefault(9)),
            null,
            ParseAniDbRating(parts.ElementAtOrDefault(3)),
            titles.ToImmutable());
    }

    private static ImmutableArray<AniDbFileEpisodeLink> ParseEpisodeLinks(
        string value,
        bool other)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];
        var tokens = value.Split('\'', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var result = ImmutableArray.CreateBuilder<AniDbFileEpisodeLink>();
        var alternatingPairs = other && tokens.Length >= 2
                               && tokens.Length % 2 == 0
                               && tokens.All(token => int.TryParse(token, out _))
                               && Enumerable.Range(0, tokens.Length / 2)
                                   .All(pairIndex => byte.TryParse(tokens[pairIndex * 2 + 1], out var percent)
                                                     && percent <= 100);
        for (var index = 0; index < tokens.Length; index++)
        {
            if (alternatingPairs)
            {
                result.Add(new AniDbFileEpisodeLink(
                    int.Parse(tokens[index], CultureInfo.InvariantCulture),
                    byte.Parse(tokens[index + 1], CultureInfo.InvariantCulture),
                    other,
                    result.Count));
                index++;
                continue;
            }
            var pair = tokens[index].Split(',', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(pair[0], out var episodeId))
                continue;
            var percentage = pair.Length == 2 && byte.TryParse(pair[1], out var explicitPercentage)
                ? explicitPercentage
                : (byte)Math.Clamp((int)Math.Round(100d / tokens.Length), 0, 100);
            result.Add(new AniDbFileEpisodeLink(episodeId, percentage, other, result.Count));
        }
        return result.ToImmutable();
    }

    private static AniDbMyListEntry? ParseMyList(UdpResponse response)
    {
        if (response.Code != 221)
            throw Unexpected(response, "MYLIST");
        var parts = response.Body.Split('|');
        if (parts.Length < 8 || !int.TryParse(parts[0], out var listId))
            throw Unexpected(response, "MYLIST");
        return new AniDbMyListEntry(
            listId,
            ParseInt(parts.ElementAtOrDefault(1)),
            ParseInt(parts.ElementAtOrDefault(2)),
            ParseInt(parts.ElementAtOrDefault(3)),
            Enum.IsDefined(typeof(AniDbMyListState), ParseInt(parts.ElementAtOrDefault(6)) ?? 0)
                ? (AniDbMyListState)(ParseInt(parts.ElementAtOrDefault(6)) ?? 0)
                : AniDbMyListState.Unknown,
            (ParseLong(parts.ElementAtOrDefault(7)) ?? 0) > 0,
            ParseEpoch(parts.ElementAtOrDefault(7)),
            ParseEpoch(parts.ElementAtOrDefault(5)));
    }

    private static AniDbMyListEntry? ParseMyListMutation(
        UdpResponse response,
        AniDbMyListState state,
        bool watched,
        DateTimeOffset? watchedAt)
    {
        if (response.Code == 210 && int.TryParse(response.Body, out var listId))
            return new AniDbMyListEntry(
                listId, null, null, null, state, watched,
                watched ? watchedAt ?? DateTimeOffset.UtcNow : null,
                DateTimeOffset.UtcNow);
        if (response.Code == 310)
            return ParseMyList(new UdpResponse(221, response.Message, response.Body));
        if (response.Code is 311 or 411 or 320 or 330)
            return null;
        throw Unexpected(response, "MYLISTADD");
    }

    private static ImmutableArray<string> SplitApostrophe(string value) =>
        value.Split('\'', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !item.Equals("none", StringComparison.OrdinalIgnoreCase))
            .ToImmutableArray();

    private static void AddTitle(
        ImmutableArray<AniDbTitle>.Builder titles,
        string language,
        string type,
        string? value)
    {
        var normalized = EmptyToNull(value ?? string.Empty);
        if (normalized == null || titles.Any(item => item.Value.Equals(normalized, StringComparison.Ordinal)))
            return;
        titles.Add(new AniDbTitle(language, type, normalized));
    }

    private static void AddResource(
        ImmutableArray<AniDbResource>.Builder resources,
        int type,
        string? identifier)
    {
        var normalized = EmptyToNull(identifier ?? string.Empty);
        if (normalized != null)
            resources.Add(new AniDbResource(type, normalized));
    }

    private static string MapRelationType(int rawType) => rawType switch
    {
        1 => "Sequel",
        2 => "Prequel",
        11 => "Same setting",
        12 => "Alternative setting",
        32 => "Alternative version",
        41 => "Music video",
        42 => "Character",
        51 => "Side story",
        52 => "Parent story",
        61 => "Summary",
        62 => "Full story",
        _ => "Other",
    };

    private static double? ParseAniDbRating(string? value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var raw)
            || raw <= 0)
            return null;
        return Math.Clamp(raw / 100d, 0d, 10d);
    }

    private static string? FormatEpochDate(string? value) => ParseEpoch(value)?.UtcDateTime
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string EscapeHash(string value)
    {
        if (value.Length != 32 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("AniDB ED2K hashes must be 32 hexadecimal characters.", nameof(value));
        return value.ToLowerInvariant();
    }

    private static int? ParseInt(string? value) => int.TryParse(value, out var result) ? result : null;
    private static long? ParseLong(string? value) => long.TryParse(value, out var result) ? result : null;
    private static DateTimeOffset? ParseEpoch(string? value)
    {
        var seconds = ParseLong(value);
        return seconds is > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds.Value) : null;
    }
    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static InvalidDataException Unexpected(UdpResponse response, string command) =>
        new($"AniDB returned unexpected {command} response {response.Code}: {response.Message}");

    private void Publish(
        AniDbClientConnectionState state,
        string? message,
        DateTimeOffset? retryAt = null)
    {
        Status = new AniDbClientStatus(state, message, _utcNow(), retryAt);
        StatusChanged?.Invoke(this, Status);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await LogoutAsync();
        }
        catch
        {
            // Best-effort logout during application shutdown.
        }
        await _transport.DisposeAsync();
        _gate.Dispose();
    }

    private sealed record UdpResponse(int Code, string Message, string Body);
}
