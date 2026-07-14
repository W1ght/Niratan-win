using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Niratan.Services.Anki;
using Niratan.Services.Audio;
using Microsoft.Data.Sqlite;

namespace Niratan.Tests.Services.Anki;

public class AnkiAudioDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_WhenSourceReturnsAudioSourceList_DownloadsResolvedAudioBytes()
    {
        var resolverUri = new Uri("http://audio.test/resolve?term=星");
        var mp3Uri = new Uri("http://audio.test/media/word.mp3");
        var mp3Bytes = new byte[] { 0x49, 0x44, 0x33, 0x04, 0x00, 0x01 };
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri == resolverUri)
            {
                return JsonResponse(
                    """
                    { "type":"audioSourceList", "audioSources":[{"url":"http://audio.test/media/word.mp3"}] }
                    """);
            }

            if (request.RequestUri == mp3Uri)
                return BytesResponse(mp3Bytes, "audio/mpeg");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var downloader = new AnkiAudioDownloader(http);

        var result = await downloader.DownloadAsync(resolverUri.ToString());

        result.Should().NotBeNull();
        result!.Bytes.Should().Equal(mp3Bytes);
        result.SourceUrl.Should().Be(mp3Uri.ToString());
        result.Filename.Should().Be($"niratan_audio_{Sha256Hex(mp3Bytes)}.mp3");
    }

    [Fact]
    public async Task DownloadAsync_UsesStableContentHashFilenameAndInfersExtensionFromContentType()
    {
        var audioUri = new Uri("http://audio.test/media/opaque");
        var audioBytes = new byte[] { 0x4f, 0x67, 0x67, 0x53, 0x01, 0x02, 0x03 };
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
            request.RequestUri == audioUri
                ? BytesResponse(audioBytes, "audio/ogg")
                : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var downloader = new AnkiAudioDownloader(http);

        var first = await downloader.DownloadAsync(audioUri.ToString());
        var second = await downloader.DownloadAsync(audioUri.ToString());

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Filename.Should().Be($"niratan_audio_{Sha256Hex(audioBytes)}.ogg");
        second!.Filename.Should().Be(first.Filename);
    }

    [Fact]
    public async Task DownloadAsync_NormalizesBackslashesFromAudioSourceListUrls()
    {
        var resolverUri = new Uri("http://audio.test/resolve");
        var normalizedUri = new Uri("http://audio.test/media/word.mp3");
        var mp3Bytes = new byte[] { 0x49, 0x44, 0x33, 0x05, 0x00, 0x02 };
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri == resolverUri)
            {
                return JsonResponse(
                    """
                    { "type":"audioSourceList", "audioSources":[{"url":"http:\\audio.test\\media\\word.mp3"}] }
                    """);
            }

            if (request.RequestUri == normalizedUri)
                return BytesResponse(mp3Bytes, "audio/mpeg");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var downloader = new AnkiAudioDownloader(http);

        var result = await downloader.DownloadAsync(resolverUri.ToString());

        result.Should().NotBeNull();
        result!.SourceUrl.Should().Be(normalizedUri.ToString());
        result.Bytes.Should().Equal(mp3Bytes);
    }

    [Fact]
    public async Task DownloadAsync_NormalizesLocalhostBeforeHttpRequest()
    {
        var expectedResolverUri = new Uri("http://127.0.0.1:5050/resolve?term=星");
        var mp3Uri = new Uri("http://audio.test/media/word.mp3");
        var mp3Bytes = new byte[] { 0x49, 0x44, 0x33, 0x05, 0x00, 0x03 };
        var sawLoopbackResolver = false;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri == expectedResolverUri)
            {
                sawLoopbackResolver = true;
                return JsonResponse(
                    """
                    { "type":"audioSourceList", "audioSources":[{"url":"http://audio.test/media/word.mp3"}] }
                    """);
            }

            if (request.RequestUri == mp3Uri)
                return BytesResponse(mp3Bytes, "audio/mpeg");

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var downloader = new AnkiAudioDownloader(http);

        var result = await downloader.DownloadAsync("http://localhost:5050/resolve?term=星");

        result.Should().NotBeNull();
        result!.Bytes.Should().Equal(mp3Bytes);
        sawLoopbackResolver.Should().BeTrue();
    }

    [Fact]
    public async Task DownloadAsync_CachesResolvedAudioSourceListResults()
    {
        var resolverUri = new Uri("http://audio.test/resolve?term=deru");
        var mp3Uri = new Uri("http://audio.test/media/word.mp3");
        var mp3Bytes = new byte[] { 0x49, 0x44, 0x33, 0x06, 0x00, 0x03 };
        var resolverHits = 0;
        var audioHits = 0;
        using var http = new HttpClient(new MapHttpMessageHandler(request =>
        {
            if (request.RequestUri == resolverUri)
            {
                resolverHits++;
                return JsonResponse(
                    """
                    { "type":"audioSourceList", "audioSources":[{"url":"http://audio.test/media/word.mp3"}] }
                    """);
            }

            if (request.RequestUri == mp3Uri)
            {
                audioHits++;
                return BytesResponse(mp3Bytes, "audio/mpeg");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        var downloader = new AnkiAudioDownloader(http);

        var first = await downloader.DownloadAsync(resolverUri.ToString());
        var second = await downloader.DownloadAsync(resolverUri.ToString());

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        second!.Filename.Should().Be(first!.Filename);
        second.Bytes.Should().Equal(first.Bytes);
        resolverHits.Should().Be(1);
        audioHits.Should().Be(1);
    }

    [Fact]
    public async Task DownloadAsync_CoalescesConcurrentAudioSourceListRequests()
    {
        var resolverUri = new Uri("http://audio.test/resolve?term=deru");
        var mp3Uri = new Uri("http://audio.test/media/word.mp3");
        var mp3Bytes = new byte[] { 0x49, 0x44, 0x33, 0x07, 0x00, 0x04 };
        var handler = new BlockingAudioSourceListHandler(
            resolverUri,
            mp3Uri,
            mp3Bytes);
        using var http = new HttpClient(handler);
        var downloader = new AnkiAudioDownloader(http);

        var results = await Task.WhenAll(
            downloader.DownloadAsync(resolverUri.ToString()),
            downloader.DownloadAsync(resolverUri.ToString()));

        results.Should().OnlyContain(result => result != null);
        results[1]!.Filename.Should().Be(results[0]!.Filename);
        results[1]!.Bytes.Should().Equal(results[0]!.Bytes);
        handler.ResolverHits.Should().Be(1);
        handler.AudioHits.Should().Be(1);
    }

    [Fact]
    public async Task DownloadAsync_WhenLocalAudioSourceListUrl_ReadsExtractedLocalAudioWithoutHttp()
    {
        var root = Path.Combine(Path.GetTempPath(), "niratan-anki-audio-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var dbPath = Path.Combine(root, "android.db");
            var cacheDir = Path.Combine(root, "cache");
            var audioBytes = new byte[] { 0x49, 0x44, 0x33, 0x08, 0x00, 0x05 };
            CreateLocalAudioDb(dbPath, "星", "ほし", "nhk16", "audio/hoshi.mp3", audioBytes);
            var localResolver = new LocalAudioSourceListResolver(() => dbPath, () => cacheDir);
            var httpHits = 0;
            using var http = new HttpClient(new MapHttpMessageHandler(_ =>
            {
                httpHits++;
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }));
            var downloader = new AnkiAudioDownloader(http, localResolver);

            var result = await downloader.DownloadAsync(
                "http://localhost:18765/localaudio/get/?term=%E6%98%9F&reading=%E3%81%BB%E3%81%97");

            result.Should().NotBeNull();
            result!.Bytes.Should().Equal(audioBytes);
            result.SourceUrl.Should().StartWith("file:///");
            result.Filename.Should().Be($"niratan_audio_{Sha256Hex(audioBytes)}.mp3");
            httpHits.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage BytesResponse(byte[] bytes, string contentType) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
            {
                Headers =
                {
                    ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType),
                },
            },
        };

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void CreateLocalAudioDb(
        string path,
        string expression,
        string reading,
        string source,
        string file,
        byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE entries (
                    id integer PRIMARY KEY NOT NULL,
                    expression text NOT NULL,
                    reading text,
                    source text NOT NULL,
                    speaker text,
                    display text,
                    file text NOT NULL
                );
                CREATE TABLE android (
                    source text NOT NULL,
                    file text NOT NULL,
                    data blob NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        using (var entry = connection.CreateCommand())
        {
            entry.CommandText = """
                INSERT INTO entries(expression, reading, source, file)
                VALUES ($expression, $reading, $source, $file);
                """;
            entry.Parameters.AddWithValue("$expression", expression);
            entry.Parameters.AddWithValue("$reading", reading);
            entry.Parameters.AddWithValue("$source", source);
            entry.Parameters.AddWithValue("$file", file);
            entry.ExecuteNonQuery();
        }

        using var audio = connection.CreateCommand();
        audio.CommandText = """
            INSERT INTO android(source, file, data)
            VALUES ($source, $file, $data);
            """;
        audio.Parameters.AddWithValue("$source", source);
        audio.Parameters.AddWithValue("$file", file);
        audio.Parameters.Add("$data", SqliteType.Blob).Value = data;
        audio.ExecuteNonQuery();
    }

    private sealed class MapHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class BlockingAudioSourceListHandler(
        Uri resolverUri,
        Uri mp3Uri,
        byte[] mp3Bytes) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _resolverEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _resolverHits;
        private int _audioHits;

        public int ResolverHits => Volatile.Read(ref _resolverHits);
        public int AudioHits => Volatile.Read(ref _audioHits);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri == resolverUri)
            {
                Interlocked.Increment(ref _resolverHits);
                _resolverEntered.TrySetResult();
                await Task.Delay(50, cancellationToken);
                return JsonResponse(
                    """
                    { "type":"audioSourceList", "audioSources":[{"url":"http://audio.test/media/word.mp3"}] }
                    """);
            }

            if (request.RequestUri == mp3Uri)
            {
                await _resolverEntered.Task.WaitAsync(cancellationToken);
                Interlocked.Increment(ref _audioHits);
                return BytesResponse(mp3Bytes, "audio/mpeg");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
