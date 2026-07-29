using System.IO.Compression;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Niratan.Models.Manga;
using Niratan.Services.Manga;
using Niratan.Tests.TestUtils;

namespace Niratan.Tests.Services.Manga;

public sealed class MihonExtensionServiceTests
{
    [Theory]
    [InlineData("http://127.0.0.1:48981")]
    [InlineData("http://localhost:48981/")]
    [InlineData("https://[::1]:48981")]
    public void NormalizeBridgeUri_AcceptsOnlyLoopback(string value)
    {
        MihonExtensionService.NormalizeBridgeUri(value)
            .Host.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("http://example.test:48981")]
    [InlineData("https://192.168.1.8:48981")]
    [InlineData("file:///tmp/bridge")]
    public void NormalizeBridgeUri_RejectsNonLoopback(string value)
    {
        var action = () => MihonExtensionService.NormalizeBridgeUri(value);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task ResolveBundledRuntime_UsesManifestPathsInsideRuntimeRoot()
    {
        using var temp = new TempDirectory();
        var javaPath = Path.Combine(temp.Path, "jre", "bin", "java.exe");
        var jarPath = Path.Combine(temp.Path, "MExtensionServer-v1.0.4.jar");
        var overlayPath = Path.Combine(
            temp.Path,
            "NiratanMExtensionOverlay.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(javaPath)!);
        await File.WriteAllBytesAsync(
            javaPath,
            [1],
            TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(
            jarPath,
            [2],
            TestContext.Current.CancellationToken);
        File.Copy(GetOverlayPath(), overlayPath);
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "runtime.json"),
            """
            {
              "schemaVersion": 2,
              "version": "1.0.4",
              "javaExecutable": "jre/bin/java.exe",
              "serverJar": "MExtensionServer-v1.0.4.jar",
              "overlayJar": "NiratanMExtensionOverlay.jar"
            }
            """,
            TestContext.Current.CancellationToken);

        var runtime = MihonExtensionService.ResolveBundledRuntime(temp.Path);

        runtime.Version.Should().Be("1.0.4");
        runtime.JavaExecutablePath.Should().Be(javaPath);
        runtime.ServerJarPath.Should().Be(jarPath);
        runtime.OverlayJarPath.Should().Be(overlayPath);
    }

    [Fact]
    public async Task ResolveBundledRuntime_RejectsManifestPathTraversal()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(
            Path.Combine(temp.Path, "runtime.json"),
            """
            {
              "schemaVersion": 2,
              "version": "1.0.4",
              "javaExecutable": "../java.exe",
              "serverJar": "MExtensionServer-v1.0.4.jar",
              "overlayJar": "NiratanMExtensionOverlay.jar"
            }
            """,
            TestContext.Current.CancellationToken);

        var action = () =>
            MihonExtensionService.ResolveBundledRuntime(temp.Path);

        action.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task RefreshRepositoriesAsync_ParsesEverySourceInMultiSourcePackages()
    {
        using var temp = new TempDirectory();
        var handler = new RecordingHandler(request =>
        {
            request.RequestUri!.AbsoluteUri.Should().Be(
                "https://repo.example/index.min.json");
            return Json(
                """
                [
                  {
                    "name":"Single",
                    "pkg":"eu.kanade.tachiyomi.extension.ja.single",
                    "version":"1.2.3",
                    "lang":"ja",
                    "nsfw":0,
                    "apk":"single.apk",
                    "sources":[
                      {"id":"10","name":"Single Source","lang":"ja","baseUrl":"https://manga.example"}
                    ]
                  },
                  {
                    "name":"Factory",
                    "pkg":"eu.kanade.tachiyomi.extension.all.factory",
                    "version":"2.0.0",
                    "lang":"all",
                    "nsfw":1,
                    "apk":"factory.apk",
                    "sources":[
                      {"id":20,"name":"First","lang":"en","baseUrl":"https://first.example"},
                      {"id":21,"name":"Second","lang":"ja","baseUrl":"https://second.example"}
                    ]
                  }
                ]
                """);
        });
        using var service = CreateService(temp, handler);

        var result = await service.RefreshRepositoriesAsync(
            new MihonExtensionConfiguration
            {
                Repositories =
                [
                    new MihonRepositoryConfiguration
                    {
                        Id = "primary",
                        Name = "Primary",
                        IndexUrl = "https://repo.example/index.min.json",
                    },
                ],
            },
            TestContext.Current.CancellationToken);
        var sources = result.Sources;

        sources.Should().HaveCount(3);
        result.Failures.Should().BeEmpty();
        var single = sources.Single(source => source.Id == "10");
        single.RepositoryId.Should().Be("primary");
        single.RepositoryName.Should().Be("Primary");
        single.ApkDownloadUrl.Should().Be(
            "https://repo.example/apk/single.apk");
        single.IconDownloadUrl.Should().Be(
            "https://repo.example/icon/eu.kanade.tachiyomi.extension.ja.single.png");
        sources.Where(source => source.PackageName.EndsWith(".factory"))
            .Should().OnlyContain(source => source.PackageSourceCount == 2);
    }

    [Fact]
    public async Task RefreshRepositoriesAsync_MergesInOrderDeduplicatesAndKeepsPartialResults()
    {
        using var temp = new TempDirectory();
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/one/index.json" => Json(
                    """
                    [
                      {
                        "name":"First package",
                        "pkg":"example.shared",
                        "version":"1.0.0",
                        "lang":"en",
                        "apk":"first.apk",
                        "sources":[
                          {"id":"1","name":"First source","lang":"en"}
                        ]
                      }
                    ]
                    """),
                "/two/index.json" => Json(
                    """
                    [
                      {
                        "name":"Duplicate package",
                        "pkg":"example.shared",
                        "version":"2.0.0",
                        "lang":"en",
                        "apk":"duplicate.apk",
                        "sources":[
                          {"id":"1","name":"Duplicate source","lang":"en"}
                        ]
                      },
                      {
                        "name":"Second package",
                        "pkg":"example.second",
                        "version":"1.0.0",
                        "lang":"ja",
                        "apk":"second.apk",
                        "sources":[
                          {"id":"2","name":"Second source","lang":"ja"}
                        ]
                      }
                    ]
                    """),
                "/bad/index.json" =>
                    new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                _ => throw new InvalidOperationException(
                    request.RequestUri.AbsolutePath),
            };
        });
        using var service = CreateService(temp, handler);

        var result = await service.RefreshRepositoriesAsync(
            new MihonExtensionConfiguration
            {
                Repositories =
                [
                    Repository("first", "First repo", "https://repo.example/one/index.json"),
                    Repository("second", "Second repo", "https://repo.example/two/index.json"),
                    Repository("bad", "Broken repo", "https://repo.example/bad/index.json"),
                ],
            },
            TestContext.Current.CancellationToken);

        result.Sources.Should().HaveCount(2);
        result.Sources.Single(source => source.Id == "1")
            .RepositoryId.Should().Be("first");
        result.Sources.Single(source => source.Id == "2")
            .RepositoryId.Should().Be("second");
        result.Failures.Should().ContainSingle()
            .Which.RepositoryId.Should().Be("bad");
    }

    [Fact]
    public async Task Configuration_LegacyRepositoryUrlMigratesToRepositoryList()
    {
        using var temp = new TempDirectory();
        var configurationPath = Path.Combine(temp.Path, "mihon.json");
        await File.WriteAllTextAsync(
            configurationPath,
            """
            {
              "RepositoryUrl": "https://raw.githubusercontent.com/keiyoushi/extensions/repo/index.min.json",
              "BridgeUrl": "http://127.0.0.1:48981"
            }
            """,
            TestContext.Current.CancellationToken);
        using var service = new MihonExtensionService(
            new HttpClient(new RecordingHandler(_ => Text("unused"))),
            configurationPath,
            Path.Combine(temp.Path, "extensions", "installed.json"),
            Path.Combine(temp.Path, "extensions"),
            Path.Combine(temp.Path, "cache"),
            Path.Combine(temp.Path, "bridge"));

        var configuration = await service.LoadConfigurationAsync(
            TestContext.Current.CancellationToken);

        configuration.Repositories.Should().ContainSingle();
        configuration.Repositories[0].Name.Should().Be("Keiyoushi");
        await service.SaveConfigurationAsync(
            configuration,
            TestContext.Current.CancellationToken);
        var persisted = await File.ReadAllTextAsync(
            configurationPath,
            TestContext.Current.CancellationToken);
        persisted.Should().Contain("\"Repositories\"");
        persisted.Should().NotContain("\"RepositoryUrl\"");
    }

    [Fact]
    public async Task Configuration_MihonLibraryRoundTripsAndDeduplicatesIdentity()
    {
        using var temp = new TempDirectory();
        using var service = CreateService(
            temp,
            new RecordingHandler(_ => Text("unused")));
        var source = new MihonInstalledExtension
        {
            SourceId = "42",
            SourceName = "Example Source",
            Lang = "ja",
            BaseUrl = "https://manga.example",
            PackageName = "eu.kanade.tachiyomi.extension.ja.example",
        };
        var manga = new MihonManga
        {
            Url = "/title/1",
            Title = " Example ",
            Author = " Author ",
            Description = " Description ",
            Genres = ["Drama", "Drama", " Romance "],
            ThumbnailUrl = " https://cdn.example/cover.jpg ",
        };
        var configuration = new MihonExtensionConfiguration
        {
            Library =
            [
                LibraryEntry(source, manga),
                LibraryEntry(source, manga),
            ],
        };

        await service.SaveConfigurationAsync(
            configuration,
            TestContext.Current.CancellationToken);
        var reloaded = await service.LoadConfigurationAsync(
            TestContext.Current.CancellationToken);

        reloaded.Library.Should().ContainSingle();
        var entry = reloaded.Library[0];
        entry.SourceId.Should().Be("42");
        entry.PackageName.Should().Be(
            "eu.kanade.tachiyomi.extension.ja.example");
        entry.Manga.Title.Should().Be("Example");
        entry.Manga.Author.Should().Be("Author");
        entry.Manga.Genres.Should().Equal("Drama", "Romance");
        entry.Manga.ThumbnailUrl.Should().Be(
            "https://cdn.example/cover.jpg");
    }

    [Fact]
    public async Task InstallAndBrowse_MultiSourceRequestCarriesSelectedSourceId()
    {
        using var temp = new TempDirectory();
        var apk = CreateApk();
        var dalvikMethods = new List<string>();
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/")
            {
                return Text("mextensionserver Server Running");
            }
            if (request.Method == HttpMethod.Get && path == "/apk/source.apk")
            {
                return Bytes(apk, "application/vnd.android.package-archive");
            }
            if (request.Method == HttpMethod.Post && path == "/dalvik")
            {
                using var document = JsonDocument.Parse(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                var method = document.RootElement.GetProperty("method").GetString()!;
                dalvikMethods.Add(method);
                document.RootElement.GetProperty("sourceId")
                    .GetString().Should().Be("42");
                Convert.FromBase64String(
                        document.RootElement.GetProperty("data").GetString()!)
                    .Should().Equal(apk);
                return method switch
                {
                    "headersManga" => Json("""["User-Agent","Niratan-Test"]"""),
                    "getPopularManga" => Json(
                        """
                        {
                          "mangas":[
                            {"url":"/title/1","title":"Example","thumbnail_url":"https://cdn.example/cover.jpg"}
                          ],
                          "hasNextPage":true
                        }
                        """),
                    "getDetailsManga" => Json(
                        """
                        {
                          "url":"/title/1",
                          "title":"Detailed Example",
                          "author":"Example Author",
                          "description":"Example description",
                          "genres":["Drama"]
                        }
                        """),
                    "getChapterList" => Json(
                        """
                        [
                          {
                            "url":"/title/1/chapter/1",
                            "name":"Chapter 1",
                            "chapter_number":1
                          }
                        ]
                        """),
                    _ => throw new InvalidOperationException(method),
                };
            }
            throw new InvalidOperationException(
                $"{request.Method} {request.RequestUri}");
        });
        using var service = CreateService(temp, handler);
        var configuration = new MihonExtensionConfiguration
        {
            BridgeUrl = "http://127.0.0.1:48981",
        };
        var source = new MihonExtensionSource
        {
            Id = "42",
            Name = "Example Source",
            Lang = "ja",
            BaseUrl = "https://manga.example",
            PackageName = "eu.kanade.tachiyomi.extension.ja.example",
            Version = "1.0.0",
            ApkFileName = "source.apk",
            ApkDownloadUrl = "https://repo.example/apk/source.apk",
            PackageSourceCount = 61,
        };

        var installed = await service.InstallAsync(
            configuration,
            source,
            TestContext.Current.CancellationToken);
        var page = await service.BrowseAsync(
            configuration,
            installed,
            query: null,
            page: 1,
            TestContext.Current.CancellationToken);
        var details = await service.GetMangaDetailsAsync(
            configuration,
            installed,
            page.MangaList[0],
            TestContext.Current.CancellationToken);
        var chapters = await service.GetChaptersAsync(
            configuration,
            installed,
            details,
            TestContext.Current.CancellationToken);

        File.Exists(installed.ApkPath).Should().BeTrue();
        installed.Sha256.Should().HaveLength(64);
        installed.Headers.Should().Contain("User-Agent", "Niratan-Test");
        page.MangaList.Should().ContainSingle()
            .Which.Title.Should().Be("Example");
        page.HasNextPage.Should().BeTrue();
        details.Title.Should().Be("Detailed Example");
        chapters.Should().ContainSingle()
            .Which.Name.Should().Be("Chapter 1");
        dalvikMethods.Should().Equal(
            "headersManga",
            "getPopularManga",
            "getDetailsManga",
            "getChapterList");

        var persisted = await service.GetInstalledSourcesAsync(
            TestContext.Current.CancellationToken);
        persisted.Should().ContainSingle()
            .Which.SourceId.Should().Be("42");
    }

    [Fact]
    public async Task GetRepositorySourceIconPathAsync_FallsBackToLargestApkRaster()
    {
        using var temp = new TempDirectory();
        var apk = CreateApk();
        var handler = new RecordingHandler(request =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/icon/example.png" => new HttpResponseMessage(
                    HttpStatusCode.NotFound),
                "/apk/example.apk" => Bytes(
                    apk,
                    "application/vnd.android.package-archive"),
                _ => throw new InvalidOperationException(
                    request.RequestUri.AbsolutePath),
            };
        });
        using var service = CreateService(temp, handler);
        var source = new MihonExtensionSource
        {
            PackageName = "example",
            ApkDownloadUrl = "https://repo.example/apk/example.apk",
            IconDownloadUrl = "https://repo.example/icon/example.png",
        };

        var iconPath = await service.GetRepositorySourceIconPathAsync(
            new MihonExtensionConfiguration(),
            source,
            TestContext.Current.CancellationToken);

        iconPath.Should().NotBeNull();
        Path.GetExtension(iconPath!).Should().Be(".png");
        File.ReadAllBytes(iconPath).Should().Equal(9, 8, 7, 6, 5);
    }

    [Fact]
    public async Task InstallAsync_PersistsSourceWhenOptionalHeadersFail()
    {
        using var temp = new TempDirectory();
        var apk = CreateApk();
        var handler = new RecordingHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/")
                return Text("mextensionserver Server Running");
            if (request.Method == HttpMethod.Get && path == "/apk/source.apk")
                return Bytes(apk, "application/vnd.android.package-archive");
            if (request.Method == HttpMethod.Post && path == "/dalvik")
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            throw new InvalidOperationException(path);
        });
        using var service = CreateService(temp, handler);

        var installed = await service.InstallAsync(
            new MihonExtensionConfiguration
            {
                BridgeUrl = "http://127.0.0.1:48981",
            },
            new MihonExtensionSource
            {
                Id = "42",
                Name = "Example",
                PackageName = "example",
                ApkDownloadUrl = "https://repo.example/apk/source.apk",
                PackageSourceCount = 1,
            },
            TestContext.Current.CancellationToken);

        installed.Headers.Should().BeEmpty();
        (await service.GetInstalledSourcesAsync(
                TestContext.Current.CancellationToken))
            .Should().ContainSingle();
    }

    private static MihonExtensionService CreateService(
        TempDirectory temp,
        HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            Path.Combine(temp.Path, "mihon.json"),
            Path.Combine(temp.Path, "extensions", "installed.json"),
            Path.Combine(temp.Path, "extensions"),
            Path.Combine(temp.Path, "cache"),
            Path.Combine(temp.Path, "bridge"));

    private static string GetOverlayPath(
        [CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            "..",
            "..",
            "..",
            "ThirdParty",
            "MExtensionServer",
            "overlay",
            "NiratanMExtensionOverlay.jar"));

    private static MihonRepositoryConfiguration Repository(
        string id,
        string name,
        string indexUrl) =>
        new()
        {
            Id = id,
            Name = name,
            IndexUrl = indexUrl,
        };

    private static MihonLibraryEntry LibraryEntry(
        MihonInstalledExtension source,
        MihonManga manga) =>
        new()
        {
            SourceId = source.SourceId,
            SourceName = source.SourceName,
            SourceLang = source.Lang,
            SourceBaseUrl = source.BaseUrl,
            PackageName = source.PackageName,
            Manga = new MihonManga
            {
                Url = manga.Url,
                Title = manga.Title,
                Artist = manga.Artist,
                Author = manga.Author,
                Description = manga.Description,
                Genres = [.. manga.Genres],
                Status = manga.Status,
                ThumbnailUrl = manga.ThumbnailUrl,
            },
        };

    private static byte[] CreateApk()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            var manifest = archive.CreateEntry("AndroidManifest.xml");
            using (var stream = manifest.Open())
                stream.Write([1, 2, 3]);
            var classes = archive.CreateEntry("classes.dex");
            using (var stream = classes.Open())
                stream.Write(Encoding.ASCII.GetBytes("dex\n035\0"));
            var smallIcon = archive.CreateEntry("res/a.png");
            using (var stream = smallIcon.Open())
                stream.Write([1, 2]);
            var largeIcon = archive.CreateEntry("res/b.png");
            using (var stream = largeIcon.Open())
                stream.Write([9, 8, 7, 6, 5]);
        }
        return output.ToArray();
    }

    private static HttpResponseMessage Json(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "application/json"),
        };

    private static HttpResponseMessage Text(string value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(value, Encoding.UTF8, "text/plain"),
        };

    private static HttpResponseMessage Bytes(byte[] value, string mediaType) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(value)
            {
                Headers = { ContentType = new(mediaType) },
            },
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
