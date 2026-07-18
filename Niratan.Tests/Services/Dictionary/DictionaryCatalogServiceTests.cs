using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Niratan.Models.Dictionary;
using Niratan.Models.Profiles;
using Niratan.Models.Settings;
using Niratan.Services.Dictionary;
using Niratan.Services.Profiles;
using Niratan.Services.Settings;

namespace Niratan.Tests.Services.Dictionary;

public sealed class DictionaryCatalogServiceTests
{
    [Theory]
    [InlineData("JMdict", "JMdict")]
    [InlineData("JMdict [2026-07-01]", "JMdict")]
    [InlineData("  Wiktionary   English-English ", "Wiktionary English-English")]
    public void DictionaryTitlesMatch_NormalizesNiratanRecommendationTitles(
        string installed,
        string recommendation)
    {
        DictionaryCatalogService.DictionaryTitlesMatch(installed, recommendation)
            .Should().BeTrue();
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsInstalledDictionaryWhenRevisionChanged()
    {
        var importService = new Mock<IDictionaryImportService>();
        importService.Setup(service => service.GetInstalledDictionariesAsync(null))
            .ReturnsAsync([
                new InstalledDictionary(
                    "JMdict",
                    DictionaryType.Term,
                    Revision: "2026-01",
                    DisplayTitle: "JMdict",
                    IndexUrl: "https://example.test/index.json")
            ]);
        var profileRuntime = new Mock<IProfileRuntimeService>();
        profileRuntime.SetupGet(service => service.ActiveLanguage)
            .Returns(ContentLanguageProfile.Japanese);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(new AppSettings());
        using var httpClient = new HttpClient(new JsonHandler("""
        {
          "title": "JMdict",
          "revision": "2026-02",
          "downloadUrl": "https://example.test/JMdict.zip"
        }
        """));
        var catalog = new DictionaryCatalogService(
            httpClient,
            importService.Object,
            profileRuntime.Object,
            settings.Object,
            NullLogger<DictionaryCatalogService>.Instance);

        var result = await catalog.CheckForUpdatesAsync(
            ct: TestContext.Current.CancellationToken);

        result.Failures.Should().BeEmpty();
        result.Updates.Should().ContainSingle();
        result.Updates[0].RemoteRevision.Should().Be("2026-02");
        result.Updates[0].DownloadUrl.Should().Be("https://example.test/JMdict.zip");
        result.CheckedCount.Should().Be(1);
    }

    [Theory]
    [InlineData(DictionaryUpdateInterval.Daily, 1)]
    [InlineData(DictionaryUpdateInterval.Weekly, 7)]
    [InlineData(DictionaryUpdateInterval.Monthly, 30)]
    public void DictionaryUpdateSettings_UsesNiratanIntervals(
        DictionaryUpdateInterval interval,
        int expectedDays)
    {
        new DictionaryUpdateSettings { Interval = interval }
            .GetInterval().Should().Be(TimeSpan.FromDays(expectedDays));
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
    }
}
