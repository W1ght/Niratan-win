using System;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Niratan.Services.Video;

internal sealed class AniDbHttpClient : IAniDbHttpClient, IDisposable
{
    private static readonly Uri Endpoint = new("http://api.anidb.net:9001/httpapi");
    private static readonly TimeSpan DefaultShortRequestInterval = TimeSpan.FromMilliseconds(2_100);
    private static readonly TimeSpan DefaultLongRequestInterval = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DefaultSustainedActivityPeriod = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultIdleResetPeriod = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultBanPeriod = TimeSpan.FromHours(12);
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromSeconds(1);
    private const int DefaultMaximumAttempts = 3;
    private readonly IAniDbConfigurationProvider _configuration;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _shortRequestInterval;
    private readonly TimeSpan _longRequestInterval;
    private readonly TimeSpan _sustainedActivityPeriod;
    private readonly TimeSpan _idleResetPeriod;
    private readonly TimeSpan _banPeriod;
    private readonly TimeSpan _retryDelay;
    private readonly int _maximumAttempts;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private DateTimeOffset? _lastRequestAt;
    private DateTimeOffset? _activityStartedAt;
    private DateTimeOffset? _banUntil;
    private readonly object _rejectedClientGate = new();
    private RejectedHttpClient? _rejectedClient;

    public DateTimeOffset? RetryAt => _banUntil is { } value && value > _utcNow() ? value : null;

    public AniDbHttpClient(IAniDbConfigurationProvider configuration)
        : this(
            configuration,
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            true,
            DefaultShortRequestInterval,
            DefaultLongRequestInterval,
            DefaultSustainedActivityPeriod,
            DefaultIdleResetPeriod,
            DefaultBanPeriod,
            DefaultRetryDelay,
            DefaultMaximumAttempts,
            static () => DateTimeOffset.UtcNow,
            static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal AniDbHttpClient(
        IAniDbConfigurationProvider configuration,
        HttpClient http,
        bool ownsHttp = false)
        : this(
            configuration,
            http,
            ownsHttp,
            DefaultShortRequestInterval,
            DefaultLongRequestInterval,
            DefaultSustainedActivityPeriod,
            DefaultIdleResetPeriod,
            DefaultBanPeriod,
            DefaultRetryDelay,
            DefaultMaximumAttempts,
            static () => DateTimeOffset.UtcNow,
            static (delay, ct) => Task.Delay(delay, ct))
    {
    }

    internal AniDbHttpClient(
        IAniDbConfigurationProvider configuration,
        HttpClient http,
        bool ownsHttp,
        TimeSpan shortRequestInterval,
        TimeSpan longRequestInterval,
        TimeSpan sustainedActivityPeriod,
        TimeSpan idleResetPeriod,
        TimeSpan banPeriod,
        TimeSpan retryDelay,
        int maximumAttempts,
        Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shortRequestInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(longRequestInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(sustainedActivityPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(idleResetPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(banPeriod, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(retryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        _configuration = configuration;
        _http = http;
        _ownsHttp = ownsHttp;
        _shortRequestInterval = shortRequestInterval;
        _longRequestInterval = longRequestInterval;
        _sustainedActivityPeriod = sustainedActivityPeriod;
        _idleResetPeriod = idleResetPeriod;
        _banPeriod = banPeriod;
        _retryDelay = retryDelay;
        _maximumAttempts = maximumAttempts;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public Task<AniDbAnime?> GetAnimeAsync(int animeId, CancellationToken ct = default) =>
        GetAnimeCoreAsync(animeId, ignoreCachedClientRejection: false, ct);

    public Task<AniDbAnime?> ProbeAnimeAsync(int animeId, CancellationToken ct = default) =>
        GetAnimeCoreAsync(animeId, ignoreCachedClientRejection: true, ct);

    private async Task<AniDbAnime?> GetAnimeCoreAsync(
        int animeId,
        bool ignoreCachedClientRejection,
        CancellationToken ct)
    {
        if (animeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(animeId));
        var configuration = await RequireConfigurationAsync(ct, ignoreCachedClientRejection);
        var document = await GetXmlAsync(BuildUri(configuration,
            $"request=anime&aid={animeId.ToString(CultureInfo.InvariantCulture)}"), ct);
        if (FindApiError(document) is { } error)
        {
            CacheRejectedClient(configuration, error);
            throw error;
        }
        ClearRejectedClient(configuration);
        return ParseAnime(document);
    }

    public async Task<ImmutableArray<AniDbMyListEntry>> GetMyListAsync(CancellationToken ct = default)
    {
        var configuration = await RequireConfigurationAsync(ct);
        var document = await GetXmlAsync(BuildUri(configuration,
            "request=mylist&user=" + Uri.EscapeDataString(configuration.Username)
            + "&pass=" + Uri.EscapeDataString(configuration.Password)), ct);
        var error = document.Root?.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase) == true
            ? document.Root
            : document.Descendants().FirstOrDefault(element =>
                element.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase));
        if (error != null)
        {
            // AniDB HTTP API uses error 330 to represent a valid empty MyList.
            var apiError = CreateApiError(error);
            if (apiError.Code == 330)
                return [];
            CacheRejectedClient(configuration, apiError);
            throw apiError;
        }
        return ParseMyList(document);
    }

    private async Task<AniDbClientConfiguration> RequireConfigurationAsync(
        CancellationToken ct,
        bool ignoreCachedClientRejection = false)
    {
        var configuration = await _configuration.GetAsync(ct)
            ?? throw new InvalidOperationException("AniDB client configuration is incomplete.");
        lock (_rejectedClientGate)
        {
            if (_rejectedClient is { } rejected)
            {
                var sameIdentity = rejected.ClientId.Equals(
                                       configuration.EffectiveHttpClientId,
                                       StringComparison.OrdinalIgnoreCase)
                                   && rejected.ClientVersion == configuration.EffectiveHttpClientVersion;
                if (sameIdentity && !ignoreCachedClientRejection)
                    throw new AniDbHttpApiException(rejected.Code);

                // A forced validation of the same identity keeps the known
                // rejection until the probe actually succeeds. A timeout or
                // malformed response must not reopen background traffic.
                if (!sameIdentity)
                    _rejectedClient = null;
            }
        }
        return configuration;
    }

    private void ClearRejectedClient(AniDbClientConfiguration configuration)
    {
        lock (_rejectedClientGate)
        {
            if (_rejectedClient is { } rejected
                && rejected.ClientId.Equals(
                    configuration.EffectiveHttpClientId,
                    StringComparison.OrdinalIgnoreCase)
                && rejected.ClientVersion == configuration.EffectiveHttpClientVersion)
            {
                _rejectedClient = null;
            }
        }
    }

    private static Uri BuildUri(AniDbClientConfiguration configuration, string query) => new(
        Endpoint + "?client=" + Uri.EscapeDataString(configuration.EffectiveHttpClientId.ToLowerInvariant())
        + "&clientver=" + configuration.EffectiveHttpClientVersion.ToString(CultureInfo.InvariantCulture)
        + "&protover=1&" + query);

    private void CacheRejectedClient(
        AniDbClientConfiguration configuration,
        AniDbHttpApiException error)
    {
        if (!error.IsClientConfigurationError)
            return;
        lock (_rejectedClientGate)
        {
            _rejectedClient = new RejectedHttpClient(
                configuration.EffectiveHttpClientId,
                configuration.EffectiveHttpClientVersion,
                error.Code);
        }
    }

    private static AniDbHttpApiException? FindApiError(XDocument document)
    {
        var error = document.Root?.DescendantsAndSelf().FirstOrDefault(element =>
            element.Name.LocalName.Equals("error", StringComparison.OrdinalIgnoreCase));
        return error == null ? null : CreateApiError(error);
    }

    private static AniDbHttpApiException CreateApiError(XElement error)
    {
        var code = Int(error.Attribute("code")?.Value);
        if (code <= 0)
            code = Int(error.Attribute("value")?.Value);
        return new AniDbHttpApiException(code, error.Value);
    }

    private async Task<XDocument> GetXmlAsync(Uri uri, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ThrowIfBanned();
            for (var attempt = 0; attempt < _maximumAttempts; attempt++)
            {
                try
                {
                    var document = await GetXmlOnceAsync(uri, ct);
                    if (IsBanned(document))
                    {
                        _banUntil = _utcNow().Add(_banPeriod);
                        throw new InvalidOperationException(
                            $"AniDB HTTP requests are paused until {_banUntil:u}.");
                    }
                    return document;
                }
                catch (Exception ex) when (attempt + 1 < _maximumAttempts && IsTransient(ex, ct))
                {
                    var retryDelay = TimeSpan.FromTicks(_retryDelay.Ticks * (1L << attempt));
                    if (retryDelay > TimeSpan.Zero)
                        await _delayAsync(retryDelay, ct);
                }
            }

            throw new InvalidOperationException("AniDB HTTP retry loop completed unexpectedly.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<XDocument> GetXmlOnceAsync(Uri uri, CancellationToken ct)
    {
        await EnforceRequestRateAsync(ct);
        try
        {
            using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength;
            if (length is > 8_388_608)
                throw new InvalidDataException("AniDB HTTP response exceeded 8 MiB.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var decoded = DecodeContent(stream, response.Content.Headers.ContentEncoding);
            using var limited = new LimitedReadStream(decoded, 8_388_608);
            using var reader = XmlReader.Create(limited, new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 8_388_608,
            });
            return await XDocument.LoadAsync(reader, LoadOptions.None, ct);
        }
        finally
        {
            _lastRequestAt = _utcNow();
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

    private void ThrowIfBanned()
    {
        if (_banUntil is not { } banUntil)
            return;
        if (banUntil <= _utcNow())
        {
            _banUntil = null;
            return;
        }
        throw new InvalidOperationException($"AniDB HTTP requests are paused until {banUntil:u}.");
    }

    private static bool IsBanned(XDocument document)
    {
        var root = document.Root;
        if (root == null)
            return false;
        return root.DescendantsAndSelf().Any(element =>
            element.Name.LocalName.Equals("banned", StringComparison.OrdinalIgnoreCase)
            || element.Value.Trim().Equals("banned", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTransient(Exception exception, CancellationToken ct)
    {
        if (exception is InvalidDataException or XmlException)
            return false;
        if (exception is OperationCanceledException)
            return !ct.IsCancellationRequested;
        if (exception is IOException or TimeoutException)
            return true;
        if (exception is not HttpRequestException httpException)
            return false;
        return httpException.StatusCode is null
               or HttpStatusCode.RequestTimeout
               or HttpStatusCode.TooManyRequests
               or HttpStatusCode.InternalServerError
               or HttpStatusCode.BadGateway
               or HttpStatusCode.ServiceUnavailable
               or HttpStatusCode.GatewayTimeout;
    }

    private static Stream DecodeContent(Stream stream, System.Collections.Generic.ICollection<string> encodings)
    {
        if (encodings.Any(value => value.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
            return new GZipStream(stream, CompressionMode.Decompress, leaveOpen: false);
        if (encodings.Any(value => value.Equals("deflate", StringComparison.OrdinalIgnoreCase)))
            return new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: false);
        return stream;
    }

    internal static AniDbAnime ParseAnime(XDocument document)
    {
        var root = document.Root ?? throw new InvalidDataException("AniDB anime XML has no root element.");
        var aid = Int(root.Attribute("id")?.Value);
        if (aid <= 0)
            throw new InvalidDataException("AniDB anime XML has no valid anime id.");
        var titles = root.Element("titles")?.Elements("title")
            .Select(element => new AniDbTitle(
                element.Attribute(XNamespace.Xml + "lang")?.Value ?? "",
                element.Attribute("type")?.Value ?? "",
                element.Value.Trim()))
            .Where(title => title.Value.Length > 0)
            .ToImmutableArray() ?? [];
        var mainTitle = titles.FirstOrDefault(title => title.Type == "main")?.Value
                        ?? root.Element("type")?.Value.Trim()
                        ?? $"AniDB {aid}";
        var originalTitle = titles.FirstOrDefault(title => title.Type == "official" && title.Language == "ja")?.Value
                            ?? titles.FirstOrDefault(title => title.Language == "x-jat")?.Value;
        var episodes = root.Element("episodes")?.Elements("episode").Select(element =>
        {
            var epno = element.Element("epno");
            var raw = epno?.Value.Trim() ?? "0";
            var type = (epno?.Attribute("type")?.Value, raw.FirstOrDefault()) switch
            {
                ("2", _) or (_, 'S') => AniDbEpisodeType.Special,
                ("3", _) or (_, 'C') => AniDbEpisodeType.Credits,
                ("4", _) or (_, 'T') => AniDbEpisodeType.Trailer,
                ("5", _) or (_, 'P') => AniDbEpisodeType.Parody,
                ("6", _) or (_, 'O') => AniDbEpisodeType.Other,
                _ => AniDbEpisodeType.Regular,
            };
            var digits = new string(raw.SkipWhile(character => !char.IsDigit(character))
                .TakeWhile(char.IsDigit).ToArray());
            var episodeTitles = element.Elements("title").Select(title => new AniDbTitle(
                title.Attribute(XNamespace.Xml + "lang")?.Value ?? "",
                "episode",
                title.Value.Trim())).Where(title => title.Value.Length > 0).ToImmutableArray();
            return new AniDbEpisode(
                Int(element.Attribute("id")?.Value), aid, type, Int(digits), raw,
                Int(element.Element("length")?.Value),
                Null(element.Element("airdate")?.Value),
                Null(element.Element("summary")?.Value),
                Double(element.Element("rating")?.Value),
                episodeTitles);
        }).Where(episode => episode.EpisodeId > 0).ToImmutableArray() ?? [];
        var relations = root.Element("relatedanime")?.Elements("anime").Select(element =>
            new AniDbRelation(aid, Int(element.Attribute("id")?.Value),
                element.Attribute("type")?.Value ?? "", Null(element.Value))
            {
                Verified = NullableBool(element.Attribute("verified")?.Value),
            })
            .Where(relation => relation.RelatedAnimeId > 0).ToImmutableArray() ?? [];
        var tags = root.Element("tags")?.Elements("tag").Select(element => new AniDbTag(
            Int(element.Attribute("id")?.Value), NullableInt(element.Attribute("parentid")?.Value),
            element.Element("name")?.Value.Trim() ?? "", Null(element.Element("description")?.Value),
            Int(element.Attribute("weight")?.Value), Bool(element.Attribute("localspoiler")?.Value),
            Bool(element.Attribute("globalspoiler")?.Value))
        {
            Verified = Bool(element.Attribute("verified")?.Value),
            UpdatedAt = Date(element.Attribute("update")?.Value),
        })
            .Where(tag => tag.TagId > 0 && tag.Name.Length > 0).ToImmutableArray() ?? [];
        var creators = root.Element("creators")?.Elements("name").Select(element => new AniDbCreator(
            Int(element.Attribute("id")?.Value), element.Value.Trim(), element.Attribute("type")?.Value ?? ""))
            .Where(creator => creator.CreatorId > 0 && creator.Name.Length > 0).ToImmutableArray() ?? [];
        var characters = root.Element("characters")?.Elements("character").Select(element =>
            new AniDbCharacter(
                Int(element.Attribute("id")?.Value),
                element.Element("name")?.Value.Trim() ?? "",
                Null(element.Element("charactertype")?.Value),
                Null(element.Attribute("type")?.Value),
                Null(element.Element("gender")?.Value),
                Null(element.Element("description")?.Value),
                Null(element.Element("picture")?.Value),
                element.Elements("seiyuu").Select(voice => new AniDbVoiceActor(
                        Int(voice.Attribute("id")?.Value),
                        voice.Value.Trim(),
                        Null(voice.Attribute("picture")?.Value)))
                    .Where(voice => voice.CreatorId > 0 && voice.Name.Length > 0)
                    .ToImmutableArray()))
            .Where(character => character.CharacterId > 0 && character.Name.Length > 0)
            .ToImmutableArray() ?? [];
        var resources = root.Element("resources")?.Elements("resource").SelectMany(element =>
        {
            var type = Int(element.Attribute("type")?.Value);
            return element.Descendants()
                .Where(child => child.Name.LocalName is "identifier" or "url")
                .Select(child => new AniDbResource(type, child.Value.Trim()));
        }).Where(resource => resource.Type > 0 && resource.Identifier.Length > 0)
            .Distinct().ToImmutableArray() ?? [];
        var similar = root.Element("similaranime")?.Elements("anime").Select(element =>
                new AniDbSimilarAnime(
                    Int(element.Attribute("id")?.Value),
                    Int(element.Attribute("approval")?.Value),
                    Int(element.Attribute("total")?.Value)))
            .Where(item => item.AnimeId > 0).ToImmutableArray() ?? [];
        var fetched = DateTimeOffset.UtcNow;
        return new AniDbAnime(
            aid, root.Element("type")?.Value.Trim() ?? "Anime", mainTitle, originalTitle,
            Null(root.Element("description")?.Value), Null(root.Element("startdate")?.Value),
            Null(root.Element("enddate")?.Value), Null(root.Element("picture")?.Value),
            Int(root.Element("episodecount")?.Value), Bool(root.Attribute("restricted")?.Value),
            Double(root.Element("ratings")?.Element("permanent")?.Value), titles, episodes,
            relations, tags, creators, fetched, fetched.AddDays(7))
        {
            Url = Null(root.Element("url")?.Value),
            Characters = characters,
            Resources = resources,
            SimilarAnime = similar,
        };
    }

    internal static ImmutableArray<AniDbMyListEntry> ParseMyList(XDocument document)
    {
        var root = document.Root?.DescendantsAndSelf()
            .FirstOrDefault(element => element.Name.LocalName.Equals("mylist", StringComparison.OrdinalIgnoreCase));
        if (root == null)
            throw new InvalidDataException("AniDB MyList XML has no mylist element.");
        return root.Elements()
            .Where(element => element.Name.LocalName.Equals("mylistitem", StringComparison.OrdinalIgnoreCase))
            .Select(element =>
            {
                var watchedAt = Date(element.Attribute("viewdate")?.Value);
                var stateValue = Int(element.Elements().FirstOrDefault(child =>
                    child.Name.LocalName.Equals("state", StringComparison.OrdinalIgnoreCase))?.Value);
                var entry = new AniDbMyListEntry(
                    NullablePositiveInt(element.Attribute("id")?.Value),
                    NullablePositiveInt(element.Attribute("fid")?.Value),
                    NullablePositiveInt(element.Attribute("eid")?.Value),
                    NullablePositiveInt(element.Attribute("aid")?.Value),
                    Enum.IsDefined(typeof(AniDbMyListState), stateValue)
                        ? (AniDbMyListState)stateValue
                        : AniDbMyListState.Unknown,
                    watchedAt != null,
                    watchedAt,
                    Date(element.Attribute("updated")?.Value))
                {
                    FileState = Math.Max(0, Int(element.Elements().FirstOrDefault(child =>
                        child.Name.LocalName.Equals("filestate", StringComparison.OrdinalIgnoreCase))?.Value)),
                };
                return entry;
            })
            .Where(entry => entry.MyListId != null)
            .ToImmutableArray();
    }

    private static int Int(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static int? NullablePositiveInt(string? value)
    {
        var result = Int(value);
        return result > 0 ? result : null;
    }
    private static int? NullableInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static double? Double(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : null;
    private static bool Bool(string? value) => value is "1" or "true" or "True";
    private static bool? NullableBool(string? value) => string.IsNullOrWhiteSpace(value) ? null : Bool(value);
    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTimeOffset? Date(string? value) => DateTimeOffset.TryParse(
        value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : null;
    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
        _gate.Dispose();
    }

    private sealed record RejectedHttpClient(string ClientId, int ClientVersion, int Code);

    private sealed class LimitedReadStream(Stream inner, long maxBytes) : Stream
    {
        private long _read;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var value = inner.Read(buffer, offset, count);
            Count(value);
            return value;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var value = await inner.ReadAsync(buffer, cancellationToken);
            Count(value);
            return value;
        }
        private void Count(int value)
        {
            _read += value;
            if (_read > maxBytes)
                throw new InvalidDataException("AniDB HTTP response exceeded 8 MiB.");
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
