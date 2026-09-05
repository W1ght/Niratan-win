using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FluentAssertions;
using Niratan.Models.Nyaa;
using Niratan.Models.QBittorrent;
using Niratan.Services.QBittorrent;

namespace Niratan.Tests.Services.QBittorrent;

public sealed class QbittorrentApiClientTests
{
    [Fact]
    public async Task TestConnection_logs_in_once_and_reuses_cookie()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/auth/login" => Response(
                HttpStatusCode.OK,
                "Ok.",
                response => response.Headers.Add("Set-Cookie", "SID=test-session; path=/")),
            "/api/v2/app/version" => Response(HttpStatusCode.OK, "v5.2.0"),
            "/api/v2/app/webapiVersion" => Response(HttpStatusCode.OK, "2.14.1"),
            _ => Response(HttpStatusCode.NotFound, "missing"),
        });
        using var http = new HttpClient(handler);
        using var client = new QbittorrentApiClient(http);

        var result = await client.TestConnectionAsync(
            new QbittorrentSettings { BaseUrl = "http://localhost:8080" },
            new QbittorrentCredentials("admin", "password", ""));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(new QbittorrentConnectionInfo("v5.2.0", "2.14.1"));
        handler.Requests.Should().HaveCount(3);
        handler.Requests[1].Headers.GetValues("Cookie").Single().Should().Be("SID=test-session");
        handler.Requests[2].Headers.GetValues("Cookie").Single().Should().Be("SID=test-session");
    }

    [Fact]
    public async Task Api_key_reads_tasks_without_login_and_maps_qb_fields()
    {
        var handler = new RecordingHandler(request =>
        {
            request.RequestUri!.AbsolutePath.Should().Be("/api/v2/torrents/info");
            request.Headers.Authorization.Should().Be(
                new AuthenticationHeaderValue("Bearer", "qbt_test-key"));
            return Response(HttpStatusCode.OK, """
                [
                  {
                    "hash": "abc123",
                    "name": "Example torrent",
                    "state": "downloading",
                    "progress": 0.25,
                    "size": 1048576,
                    "amount_left": 786432,
                    "dlspeed": 2048,
                    "upspeed": 512,
                    "eta": 120,
                    "ratio": 0.5,
                    "category": "niratan",
                    "tags": "NIRATAN",
                    "save_path": "C:/Downloads",
                    "content_path": "C:/Downloads/Example",
                    "added_on": 1760000000,
                    "completion_on": 0
                  }
                ]
                """);
        });
        using var http = new HttpClient(handler);
        using var client = new QbittorrentApiClient(http);

        var result = await client.GetTorrentsAsync(
            new QbittorrentSettings { BaseUrl = "https://qb.example.test" },
            new QbittorrentCredentials("", "", "qbt_test-key"));

        result.IsSuccess.Should().BeTrue();
        var torrent = result.Value.Should().ContainSingle().Subject;
        torrent.Hash.Should().Be("abc123");
        torrent.ProgressPercent.Should().Be(25);
        torrent.DownloadRateBytesPerSecond.Should().Be(2048);
        torrent.Category.Should().Be("niratan");
        handler.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task GetTorrentDetails_reads_properties_files_and_trackers()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/torrents/properties" => Response(HttpStatusCode.OK, """
                {
                  "save_path": "C:/Downloads",
                  "creation_date": 1760000000,
                  "piece_size": 524288,
                  "comment": "test comment",
                  "total_wasted": 12,
                  "total_uploaded": 2048,
                  "total_downloaded": 4096,
                  "dl_speed_avg": 100,
                  "up_speed_avg": 20,
                  "eta": 60,
                  "peers": 3,
                  "peers_total": 8,
                  "seeds": 2,
                  "seeds_total": 5,
                  "pieces_have": 10,
                  "pieces_num": 20,
                  "nb_connections": 4,
                  "nb_connections_limit": 50,
                  "share_ratio": 0.5,
                  "total_size": 8192,
                  "isPrivate": true,
                  "created_by": "test",
                  "addition_date": 1760000001,
                  "completion_date": 0
                }
                """),
            "/api/v2/torrents/files" => Response(HttpStatusCode.OK, """
                [
                  {"index":0,"name":"folder/file.mkv","size":8192,"progress":0.5,"priority":1,"is_seed":false,"availability":1.0}
                ]
                """),
            "/api/v2/torrents/trackers" => Response(HttpStatusCode.OK, """
                [
                  {"url":"https://tracker.example/announce","status":2,"tier":0,"num_peers":3,"num_seeds":2,"num_leeches":1,"num_downloaded":4,"msg":""}
                ]
                """),
            _ => Response(HttpStatusCode.NotFound, "missing"),
        });
        using var http = new HttpClient(handler);
        using var client = new QbittorrentApiClient(http);

        var result = await client.GetTorrentDetailsAsync(
            new QbittorrentSettings { BaseUrl = "https://qb.example.test" },
            new QbittorrentCredentials("", "", "qbt_test-key"),
            "abcdef0123456789abcdef0123456789abcdef01");

        result.IsSuccess.Should().BeTrue();
        result.Value!.Properties.PiecesHave.Should().Be(10);
        result.Value.Files.Should().ContainSingle().Which.Name.Should().Be("folder/file.mkv");
        result.Value.Trackers.Should().ContainSingle().Which.Status.Should().Be(2);
        handler.Requests.Should().HaveCount(3);
        handler.Requests.Should().OnlyContain(request =>
            request.Headers.Authorization != null
            && request.Headers.Authorization.Scheme == "Bearer"
            && request.Headers.Authorization.Parameter == "qbt_test-key");
    }

    [Fact]
    public async Task AddTorrent_sends_allowed_nyaa_url_and_download_options()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/v2/auth/login" => Response(
                HttpStatusCode.OK,
                "Ok.",
                response => response.Headers.Add("Set-Cookie", "SID=add-session; path=/")),
            "/api/v2/torrents/add" => Response(HttpStatusCode.OK, "Ok."),
            _ => Response(HttpStatusCode.NotFound, "missing"),
        });
        using var http = new HttpClient(handler);
        using var client = new QbittorrentApiClient(http);
        var item = new NyaaTorrentItem(
            "123",
            "Example",
            new Uri("https://nyaa.si/download/123.torrent"),
            new Uri("https://nyaa.si/view/123"),
            "Anime",
            1024,
            10,
            1,
            5,
            DateTimeOffset.UtcNow,
            true,
            false);

        var result = await client.AddTorrentAsync(
            new QbittorrentSettings
            {
                BaseUrl = "http://127.0.0.1:8080",
                DefaultSavePath = "C:/Downloads",
                DefaultCategory = "niratan",
                AddPaused = true,
            },
            new QbittorrentCredentials("admin", "password", ""),
            item);

        result.IsSuccess.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
        var body = handler.Bodies[1];
        body.Should().Contain("https://nyaa.si/download/123.torrent");
        body.Should().Contain("name=tags");
        body.Should().Contain("NIRATAN");
        body.Should().Contain("name=paused");
        body.Should().Contain("true");
        body.Should().Contain("name=savepath");
        body.Should().Contain("C:/Downloads");
        body.Should().Contain("name=category");
        body.Should().Contain("niratan");
    }

    [Theory]
    [InlineData("https://nyaa.si/download/123.torrent?redirect=https://example.com/evil")]
    [InlineData("https://nyaa.si/download/123.torrent#fragment")]
    public async Task AddTorrent_rejects_nyaa_urls_with_query_or_fragment(string torrentUrl)
    {
        var handler = new RecordingHandler(_ => Response(HttpStatusCode.OK, "Ok."));
        using var http = new HttpClient(handler);
        using var client = new QbittorrentApiClient(http);
        var item = new NyaaTorrentItem(
            "123",
            "Example",
            new Uri(torrentUrl),
            new Uri("https://nyaa.si/view/123"),
            "Anime",
            1024,
            10,
            1,
            5,
            DateTimeOffset.UtcNow,
            true,
            false);

        var result = await client.AddTorrentAsync(
            new QbittorrentSettings { BaseUrl = "http://127.0.0.1:8080" },
            new QbittorrentCredentials("admin", "password", ""),
            item);

        result.IsSuccess.Should().BeFalse();
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Remote_http_endpoint_is_rejected_before_request()
    {
        var handler = new RecordingHandler(_ => Response(HttpStatusCode.OK, "[]"));
        using var http = new HttpClient(handler);
        using var client = new QbittorrentApiClient(http);

        var result = await client.GetTorrentsAsync(
            new QbittorrentSettings { BaseUrl = "http://192.168.1.20:8080" },
            new QbittorrentCredentials("", "", "qbt_test-key"));

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("HTTPS");
        handler.Requests.Should().BeEmpty();
    }

    private static HttpResponseMessage Response(
        HttpStatusCode status,
        string body,
        Action<HttpResponseMessage>? configure = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body),
        };
        configure?.Invoke(response);
        return response;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "");
            return Task.FromResult(responseFactory(request));
        }
    }
}
