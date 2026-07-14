using System.Net;
using System.Text.Json;
using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Anki;

namespace Niratan.Tests.Services.Anki;

public class AnkiConnectClientTests
{
    [Fact]
    public async Task StoreMediaFilesAsync_UnwrapsMultiActionResultWrappers()
    {
        using var client = new AnkiConnectClient(
            "http://anki.test",
            new JsonHttpMessageHandler("""
            {
              "result": [
                { "result": "stored.mp3", "error": null }
              ],
              "error": null
            }
            """));

        var stored = await client.StoreMediaFilesAsync([("original.mp3", [1, 2, 3])]);

        stored.Should().ContainSingle().Which.Should().Be("stored.mp3");
    }

    [Fact]
    public async Task StoreMediaFilesAsync_WhenMultiActionErrorIsPresent_ReturnsEmptyStoredName()
    {
        using var client = new AnkiConnectClient(
            "http://anki.test",
            new JsonHttpMessageHandler("""
            {
              "result": [
                { "result": null, "error": "media write failed" }
              ],
              "error": null
            }
            """));

        var stored = await client.StoreMediaFilesAsync([("original.mp3", [1, 2, 3])]);

        stored.Should().ContainSingle().Which.Should().Be("");
    }

    [Fact]
    public async Task StoreMediaFilesAsync_PreservesSuccessfulBatchItemsWhenAnotherActionFails()
    {
        using var client = new AnkiConnectClient(
            "http://anki.test",
            new JsonHttpMessageHandler("""
            {
              "result": [
                { "result": "stored-a.mp3", "error": null },
                { "result": null, "error": "media write failed" }
              ],
              "error": null
            }
            """));

        var stored = await client.StoreMediaFilesAsync(
            [
                ("a.mp3", [1, 2, 3]),
                ("b.mp3", [4, 5, 6]),
            ]);

        stored.Should().Equal("stored-a.mp3", "");
    }

    [Fact]
    public async Task AddNoteWithOptionalSyncAsync_SucceedsWhenSyncFailsAfterAddNoteSucceeds()
    {
        using var client = new AnkiConnectClient(
            "http://anki.test",
            new JsonHttpMessageHandler("""
            {
              "result": [
                { "result": 123456789, "error": null },
                { "result": null, "error": "sync failed" }
              ],
              "error": null
            }
            """));
        var deck = new AnkiDeck { Name = "Mining", Id = 1 };
        var noteType = new AnkiNoteType { Name = "Basic", Id = 2 };

        var success = await client.AddNoteWithOptionalSyncAsync(
            deck,
            noteType,
            new Dictionary<string, string> { ["Front"] = "星" },
            new AnkiSettings(),
            sync: true);

        success.Should().BeTrue();
    }

    [Fact]
    public async Task AddNoteWithOptionalSyncAsync_TreatsNullActionErrorAsSuccess()
    {
        using var client = new AnkiConnectClient(
            "http://anki.test",
            new JsonHttpMessageHandler("""
            {
              "result": [
                { "result": 123456789, "error": null }
              ],
              "error": null
            }
            """));
        var deck = new AnkiDeck { Name = "Mining", Id = 1 };
        var noteType = new AnkiNoteType { Name = "Basic", Id = 2 };

        var success = await client.AddNoteWithOptionalSyncAsync(
            deck,
            noteType,
            new Dictionary<string, string> { ["Front"] = "星" },
            new AnkiSettings(),
            sync: false);

        success.Should().BeTrue();
    }

    private sealed class JsonHttpMessageHandler(string responseJson) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            JsonDocument.Parse(body).RootElement.GetProperty("action").GetString().Should().Be("multi");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
