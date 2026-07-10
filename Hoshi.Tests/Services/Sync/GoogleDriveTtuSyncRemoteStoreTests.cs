using System.Net;
using FluentAssertions;
using Hoshi.Models.Sync;
using Hoshi.Services.Sync;

namespace Hoshi.Tests.Services.Sync;

public sealed class GoogleDriveTtuSyncRemoteStoreTests
{
    [Fact]
    public async Task ListRemoteBooksAsync_ReturnsFoldersThatContainBookData()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new QueuedHttpMessageHandler(
            JsonResponse("""{"files":[{"id":"root-folder","name":"ttu-reader-data"}]}"""),
            JsonResponse("""
                {
                  "files": [
                    { "id": "book-folder-1", "name": "星~ttu-star~読む%2F" },
                    { "id": "book-folder-2", "name": "NoBookData" }
                  ]
                }
                """),
            JsonResponse("""
                {
                  "files": [
                    { "id": "bookdata-id", "name": "bookdata_1_6_1200_2000_1000.zip", "parents": ["book-folder-1"] },
                    { "id": "progress-id", "name": "progress_1_6_2000_0.5.json", "parents": ["book-folder-1"] },
                    { "id": "orphan-progress", "name": "progress_1_6_1000_0.1.json", "parents": ["book-folder-2"] }
                  ]
                }
                """));
        var store = CreateStore(handler);

        var books = await store.ListRemoteBooksAsync(ct);

        books.Should().ContainSingle();
        books[0].Id.Should().Be("book-folder-1");
        books[0].Title.Should().Be("星*読む/");
        books[0].SanitizedTitle.Should().Be("星~ttu-star~読む%2F");
        books[0].Files.BookData.Should().Be(new TtuRemoteFile("bookdata-id", "bookdata_1_6_1200_2000_1000.zip", "book-folder-1", null));
        books[0].Progress.Should().Be(0.5);
        Uri.UnescapeDataString(handler.Requests[2].RequestUri!.Query)
            .Should()
            .Contain("('book-folder-1' in parents or 'book-folder-2' in parents)");
    }

    [Fact]
    public async Task DownloadBookDataAsync_DownloadsAltMediaToDestinationFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new QueuedHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3, 4]),
        });
        using var temp = new TempFile();
        var progressValues = new List<double>();
        var store = CreateStore(handler);

        await store.DownloadBookDataAsync(
            new TtuRemoteFile("bookdata-id", "bookdata_1_6_1200_2000_1000.zip"),
            temp.Path,
            new Progress<double>(value => progressValues.Add(value)),
            ct);

        var bytes = await File.ReadAllBytesAsync(temp.Path, ct);
        bytes.Should().Equal([1, 2, 3, 4]);
        handler.Requests.Single().RequestUri!.ToString().Should().Contain("/drive/v3/files/bookdata-id?alt=media");
        progressValues.Should().Contain(value => Math.Abs(value - 1) < 0.0001);
    }

    [Fact]
    public async Task ListBookFilesAsync_FindsTtuRootAndSanitizedBookFolder()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new QueuedHttpMessageHandler(
            JsonResponse("""{"files":[{"id":"root-folder","name":"ttu-reader-data"}]}"""),
            JsonResponse("""{"files":[{"id":"book-folder","name":"星~ttu-star~読む%2F"}]}"""),
            JsonResponse("""
                {
                  "files": [
                    { "id": "progress-id", "name": "progress_1_6_2000_0.25.json" },
                    { "id": "statistics-id", "name": "statistics_1_6_3_40_2_0_0_0_0_1_1_20_20_7200_7200_na.json" },
                    { "id": "audio-id", "name": "audioBook_1_6_3000_42.5.json" }
                  ]
                }
                """));
        var store = CreateStore(handler);

        var files = await store.ListBookFilesAsync("星*読む/", ct);

        files.Progress.Should().Be(new TtuRemoteFile("progress-id", "progress_1_6_2000_0.25.json"));
        files.Statistics.Should().Be(new TtuRemoteFile("statistics-id", "statistics_1_6_3_40_2_0_0_0_0_1_1_20_20_7200_7200_na.json"));
        files.AudioBook.Should().Be(new TtuRemoteFile("audio-id", "audioBook_1_6_3000_42.5.json"));
        handler.Requests.Should().OnlyContain(request =>
            request.Headers.Authorization != null
            && request.Headers.Authorization.Scheme == "Bearer"
            && request.Headers.Authorization.Parameter == "drive-token");
        Uri.UnescapeDataString(handler.Requests[0].RequestUri!.Query).Should().Contain("name = 'ttu-reader-data'");
        Uri.UnescapeDataString(handler.Requests[1].RequestUri!.Query).Should().Contain("name = '星~ttu-star~読む%2F'");
    }

    [Fact]
    public async Task GetProgressAsync_DownloadsMediaAndReadsUnixMilliseconds()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new QueuedHttpMessageHandler(JsonResponse("""
            {
              "dataId": 7,
              "exploredCharCount": 456,
              "progress": 0.25,
              "lastBookmarkModified": 2000
            }
            """));
        var store = CreateStore(handler);

        var progress = await store.GetProgressAsync(
            new TtuRemoteFile("progress-id", "progress_1_6_2000_0.25.json"),
            ct);

        handler.Requests[0].RequestUri!.ToString().Should().Contain("/drive/v3/files/progress-id?alt=media");
        progress.Should().Be(new TtuProgress(
            DataId: 7,
            ExploredCharCount: 456,
            Progress: 0.25,
            LastBookmarkModified: DateTimeOffset.FromUnixTimeMilliseconds(2000)));
    }

    [Fact]
    public async Task UpsertProgressAsync_CreatesMultipartJsonFileWhenNoExistingFile()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new QueuedHttpMessageHandler(
            JsonResponse("""{"files":[{"id":"root-folder","name":"ttu-reader-data"}]}"""),
            JsonResponse("""{"files":[{"id":"book-folder","name":"星を読む"}]}"""),
            JsonResponse("""{"id":"progress-id","name":"progress_1_6_2000_0.25.json"}"""));
        var store = CreateStore(handler);

        await store.UpsertProgressAsync(
            "星を読む",
            new TtuProgress(
                DataId: 7,
                ExploredCharCount: 456,
                Progress: 0.25,
                LastBookmarkModified: DateTimeOffset.FromUnixTimeMilliseconds(2000)),
            existingFile: null,
            ct);

        var uploadRequest = handler.Requests[^1];
        uploadRequest.Method.Should().Be(HttpMethod.Post);
        uploadRequest.RequestUri!.ToString().Should().Contain("/upload/drive/v3/files?uploadType=multipart");
        handler.Bodies[^1].Should().Contain("\"name\":\"progress_1_6_2000_0.25.json\"");
        handler.Bodies[^1].Should().Contain("\"parents\":[\"book-folder\"]");
        handler.Bodies[^1].Should().Contain("\"exploredCharCount\":456");
        handler.Bodies[^1].Should().Contain("\"lastBookmarkModified\":2000");
    }

    [Fact]
    public async Task UpsertAudioBookAsync_PatchesExistingFileWithTtuFileName()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new QueuedHttpMessageHandler(JsonResponse("""{"id":"audio-id","name":"audioBook_1_6_3000_42.5.json"}"""));
        var store = CreateStore(handler);

        await store.UpsertAudioBookAsync(
            "星を読む",
            new TtuAudioBook("星を読む", PlaybackPosition: 42.5, LastAudioBookModified: 3000),
            new TtuRemoteFile("audio-id", "audioBook_1_6_1000_1.json"),
            ct);

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Patch);
        request.RequestUri!.ToString().Should().Contain("/upload/drive/v3/files/audio-id?uploadType=multipart");
        handler.Bodies.Single().Should().Contain("\"name\":\"audioBook_1_6_3000_42.5.json\"");
        handler.Bodies.Single().Should().Contain("\"playbackPosition\":42.5");
    }

    private static GoogleDriveTtuSyncRemoteStore CreateStore(QueuedHttpMessageHandler handler) =>
        new(
            new FakeGoogleDriveAuthService(),
            new HttpClient(handler),
            new GoogleDriveSyncCache());

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };

    private sealed class FakeGoogleDriveAuthService : IGoogleDriveAuthService
    {
        public bool HasCredentials => true;

        public Task AuthenticateAsync(
            string clientId,
            string clientSecret,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) =>
            Task.FromResult("drive-token");

        public Task SignOutAsync(CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class QueuedHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueuedHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content == null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken));
            _responses.Should().NotBeEmpty("a response should be queued for {0}", request.RequestUri);
            return _responses.Dequeue();
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{Guid.NewGuid():N}.zip");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
