using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Niratan.Models.ZLibrary;
using Niratan.Services.ZLibrary;

namespace Niratan.Tests.Services.ZLibrary;

public sealed class ZLibraryClientTests
{
    [Fact]
    public async Task LoginAndGeneralSearch_UsesApiFiltersAndParsesTrueBookCount()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/eapi/user/login" => JsonResponse(
                """
                {"success":1,"user":{"id":42,"remix_userkey":"session-key"}}
                """),
            "/eapi/book/search" => JsonResponse(
                """
                {
                  "success": 1,
                  "books": [{
                    "id": "epub-1",
                    "hash": "book-hash",
                    "title": "吾輩は猫である",
                    "author": "夏目漱石",
                    "language": "Japanese",
                    "year": 1905,
                    "extension": "EPUB",
                    "filesize": 12288,
                    "filesizeString": "12 KB",
                    "cover": "https://covers.example/cat.jpg"
                  }],
                  "exactMatch": { "books": [{
                    "id": "epub-1",
                    "hash": "book-hash",
                    "title": "吾輩は猫である",
                    "author": "夏目漱石",
                    "language": "Japanese",
                    "year": 1905,
                    "extension": "EPUB",
                    "filesize": 12288,
                    "filesizeString": "12 KB",
                    "cover": "https://covers.example/cat.jpg"
                  }] },
                  "exactBooksCount": 320,
                  "pagination": { "total_items": 400, "total_pages": 7 }
                }
                """),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var sut = new ZLibraryClient(http);

        var session = await sut.LoginAsync(
            new ZLibraryCredentials("https://books.example/path", "reader@example.com", "secret"),
            TestContext.Current.CancellationToken);
        var result = await sut.SearchAsync(session, new ZLibrarySearchOptions(
            "猫",
            ExactMatching: true,
            YearFrom: 1900,
            YearTo: 1910,
            Language: "japanese",
            Extension: "EPUB"),
            2,
            TestContext.Current.CancellationToken);

        session.BaseUri.Should().Be(new Uri("https://books.example/"));
        session.UserId.Should().Be("42");
        session.UserKey.Should().Be("session-key");
        result.Page.Should().Be(2);
        result.TotalCount.Should().Be(320);
        result.TotalCountLabel.Should().Be("320");
        result.TotalPages.Should().Be(7);
        result.Books.Should().ContainSingle();
        result.Books[0].Title.Should().Be("吾輩は猫である");
        result.Books[0].FileSize.Should().Be(12 * 1024);
        result.Books[0].Hash.Should().Be("book-hash");

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Body.Should().Contain("email=reader%40example.com");
        handler.Requests[0].Body.Should().Contain("password=secret");
        handler.Requests[1].Method.Should().Be(HttpMethod.Post);
        handler.Requests[1].Path.Should().Be("/eapi/book/search");
        handler.Requests[1].Body.Should().Contain("message=%E7%8C%AB");
        handler.Requests[1].Body.Should().Contain("page=2");
        handler.Requests[1].Body.Should().Contain("limit=50");
        handler.Requests[1].Body.Should().Contain("yearFrom=1900");
        handler.Requests[1].Body.Should().Contain("yearTo=1910");
        handler.Requests[1].Body.Should().Contain("languages%5B0%5D=japanese");
        handler.Requests[1].Body.Should().Contain("extensions%5B0%5D=epub");
        handler.Requests[1].Cookie.Should().Be(
            "remix_userid=42; remix_userkey=session-key");
        handler.Requests[1].UserAgent.Should().StartWith("Niratan/");
        handler.Requests[1].XRequestedWith.Should().Be("XMLHttpRequest");
    }

    [Fact]
    public async Task GeneralSearch_RetriesTransientGatewayFailures()
    {
        var searchAttempts = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath != "/eapi/book/search")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            searchAttempts++;
            return searchAttempts <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : JsonResponse(
                    """
                    {"success":1,"books":[{"id":"1","hash":"hash","title":"Cat",
                    "author":"Author","language":"English","extension":"EPUB",
                    "filesize":1024,"filesizeString":"1 KB"}],
                    "pagination":{"total_items":1,"total_pages":1}}
                    """);
        });
        using var http = new HttpClient(handler);
        using var sut = new ZLibraryClient(
            http,
            delayAsync: (_, _) => Task.CompletedTask);
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");

        var result = await sut.SearchAsync(
            session,
            new ZLibrarySearchOptions("cat"),
            ct: TestContext.Current.CancellationToken);

        result.Books.Should().ContainSingle();
        searchAttempts.Should().Be(3);
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task Search_WhenServerReturnsDiamWallChallenge_ReportsActionableError()
    {
        var challenge = new HttpResponseMessage((HttpStatusCode)513)
        {
            Content = new StringContent(
                "<html><title>Verifying your browser | DiamWall</title></html>",
                Encoding.UTF8,
                "text/html"),
        };
        challenge.Headers.TryAddWithoutValidation("Server", "DiamWall");
        using var http = new HttpClient(new RecordingHandler(_ => challenge));
        using var sut = new ZLibraryClient(http);
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");

        var action = () => sut.SearchAsync(
            session,
            new ZLibrarySearchOptions("cat"),
            ct: TestContext.Current.CancellationToken);

        var exception = await action.Should().ThrowAsync<ZLibraryException>();
        exception.Which.Message.Should().Contain("browser verification");
        exception.Which.Message.Should().Contain("Z-Access");
    }

    [Fact]
    public async Task DownloadEpubAsync_ResolvesDownloadLinkAndStreamsFile()
    {
        var payload = Encoding.UTF8.GetBytes("PK\u0003\u0004epub-data");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/eapi/book/12/abc/file" => JsonResponse(
                """
                {"success":1,"file":{"allowDownload":true,"downloadLink":"/download/book.epub"}}
                """),
            "/download/book.epub" => BinaryResponse(payload, "application/epub+zip"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var sut = new ZLibraryClient(http);
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");
        var book = new ZLibraryBook(
            "12", "abc", "Book", "Author", "EPUB", "1 KB", payload.Length,
            "English", 2025, null);
        await using var destination = new MemoryStream();

        await sut.DownloadEpubAsync(
            session,
            book,
            destination,
            TestContext.Current.CancellationToken);

        destination.ToArray().Should().Equal(payload);
        handler.Requests.Select(request => request.Path).Should().Equal(
            "/eapi/book/12/abc/file",
            "/download/book.epub");
        handler.Requests.Should().OnlyContain(request =>
            request.Cookie == "remix_userid=42; remix_userkey=key");
    }

    [Fact]
    public async Task DownloadEpubAsync_UsesDirectPathReturnedByWebSearch()
    {
        var payload = Encoding.UTF8.GetBytes("PK\u0003\u0004epub-data");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/dl/direct-token" => BinaryResponse(payload, "application/epub+zip"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var sut = new ZLibraryClient(http);
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");
        var book = new ZLibraryBook(
            "12", string.Empty, "Book", "Author", "EPUB", "1 KB", payload.Length,
            "English", 2025, null, "/dl/direct-token", "/book/12/title.html");
        await using var destination = new MemoryStream();

        await sut.DownloadEpubAsync(
            session,
            book,
            destination,
            TestContext.Current.CancellationToken);

        destination.ToArray().Should().Equal(payload);
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Path.Should().Be("/dl/direct-token");
        handler.Requests[0].Cookie.Should().Be("remix_userid=42; remix_userkey=key");
    }

    [Theory]
    [InlineData("http://books.example")]
    [InlineData("not a URL")]
    [InlineData("https://name:password@books.example")]
    public async Task LoginAsync_RejectsUnsafeBaseAddress(string baseUrl)
    {
        using var http = new HttpClient(new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)));
        using var sut = new ZLibraryClient(http);

        var action = () => sut.LoginAsync(
            new ZLibraryCredentials(baseUrl, "reader@example.com", "secret"));

        await action.Should().ThrowAsync<ZLibraryException>();
    }

    [Fact]
    public async Task DownloadEpubAsync_RejectsHtmlInsteadOfBook()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/eapi/book/12/abc/file" => JsonResponse(
                """
                {"success":1,"file":{"allowDownload":true,"downloadLink":"/download/book.epub"}}
                """),
            _ => BinaryResponse(Encoding.UTF8.GetBytes("<html>limit</html>"), "text/html"),
        });
        using var http = new HttpClient(handler);
        using var sut = new ZLibraryClient(http);
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");
        var book = new ZLibraryBook(
            "12", "abc", "Book", "Author", "EPUB", "1 KB", null,
            "English", null, null);

        var action = () => sut.DownloadEpubAsync(session, book, new MemoryStream());

        await action.Should().ThrowAsync<ZLibraryException>()
            .WithMessage("*web page instead of an EPUB*");
    }

    [Fact]
    public async Task DownloadEpubAsync_DropsSessionCookiesOnCrossOriginRedirect()
    {
        var payload = Encoding.UTF8.GetBytes("PK\u0003\u0004epub-data");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/eapi/book/12/abc/file" => JsonResponse(
                """
                {"success":1,"file":{"allowDownload":true,"downloadLink":"/download/book.epub"}}
                """),
            "/download/book.epub" => new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri("https://cdn.example/book.epub") },
            },
            "/book.epub" => BinaryResponse(payload, "application/epub+zip"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var sut = new ZLibraryClient(http);
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");
        var book = new ZLibraryBook(
            "12", "abc", "Book", "Author", "EPUB", "1 KB", null,
            "English", null, null);
        await using var destination = new MemoryStream();

        await sut.DownloadEpubAsync(
            session,
            book,
            destination,
            TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(3);
        handler.Requests[1].Cookie.Should().NotBeNull();
        handler.Requests[2].Cookie.Should().BeNull();
        destination.ToArray().Should().Equal(payload);
    }

    [Fact]
    public async Task DownloadEpubAsync_DoesNotSendSessionCookieToDirectCdnLink()
    {
        var payload = Encoding.UTF8.GetBytes("PK\u0003\u0004epub-data");
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/eapi/book/12/abc/file" => JsonResponse(
                """
                {"success":1,"file":{"allowDownload":true,"downloadLink":"https://cdn.example/book.epub"}}
                """),
            "/book.epub" => BinaryResponse(payload, "application/epub+zip"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var http = new HttpClient(handler);
        using var sut = new ZLibraryClient(http);
        var session = new ZLibrarySession(new Uri("https://books.example/"), "42", "key");
        var book = new ZLibraryBook(
            "12", "abc", "Book", "Author", "EPUB", "1 KB", null,
            "English", null, null);
        await using var destination = new MemoryStream();

        await sut.DownloadEpubAsync(
            session,
            book,
            destination,
            TestContext.Current.CancellationToken);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Cookie.Should().NotBeNull();
        handler.Requests[1].Cookie.Should().BeNull();
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage BinaryResponse(byte[] bytes, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return response;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RequestSnapshot> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RequestSnapshot(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Headers.TryGetValues("Cookie", out var values)
                    ? values.Single()
                    : null,
                request.Headers.TryGetValues("User-Agent", out var userAgents)
                    ? string.Join(" ", userAgents)
                    : null,
                request.Headers.TryGetValues("Accept", out var acceptValues)
                    ? string.Join(",", acceptValues)
                    : null,
                request.Headers.Referrer?.AbsoluteUri,
                request.Headers.TryGetValues(
                    "Upgrade-Insecure-Requests",
                    out var upgradeValues)
                        ? upgradeValues.Single()
                        : null,
                request.Headers.TryGetValues("X-Requested-With", out var requestedWithValues)
                    ? requestedWithValues.Single()
                    : null));
            return responder(request);
        }
    }

    private sealed record RequestSnapshot(
        HttpMethod Method,
        string Path,
        string Query,
        string Body,
        string? Cookie,
        string? UserAgent,
        string? Accept,
        string? Referrer,
        string? UpgradeInsecureRequests,
        string? XRequestedWith);
}
