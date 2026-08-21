using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class RemoteVideoHttpRangeStreamBridgeTests
{
    [Fact]
    public void TryCreateSource_OnlyAcceptsSizedHttpsGoogleVideoStreams()
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = "Example/1.0, compatible",
        };

        MpvHttpRangeStreamBridge.TryCreateSource(
                "https://rr1---sn.example.googlevideo.com/videoplayback?clen=128&expire=1",
                headers,
                out var source)
            .Should().BeTrue();
        source!.ContentLength.Should().Be(128);
        source.Headers.Should().Contain(headers);

        MpvHttpRangeStreamBridge.TryCreateSource(
                "http://rr1---sn.example.googlevideo.com/videoplayback?clen=128",
                headers,
                out _)
            .Should().BeFalse();
        MpvHttpRangeStreamBridge.TryCreateSource(
                "https://googlevideo.com.example.test/videoplayback?clen=128",
                headers,
                out _)
            .Should().BeFalse();
        MpvHttpRangeStreamBridge.TryCreateSource(
                "https://rr1---sn.example.googlevideo.com/videoplayback",
                headers,
                out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Prepare_RewritesEligibleVideoAndAudioToOpaqueCallbackUris()
    {
        using var client = new HttpClient(new ByteRangeHandler(new byte[64]));
        using var bridge = new MpvHttpRangeStreamBridge(client);
        var request = new VideoPlaybackRequest(
            "https://video.googlevideo.com/videoplayback?clen=64&signature=video-secret",
            "https://audio.googlevideo.com/videoplayback?clen=32&signature=audio-secret",
            null,
            new Dictionary<string, string>(),
            null);

        var prepared = bridge.Prepare(request);

        prepared.PrimarySource.Should().StartWith($"{MpvHttpRangeStreamBridge.Protocol}://");
        prepared.ExternalAudioSource.Should().StartWith($"{MpvHttpRangeStreamBridge.Protocol}://");
        prepared.PrimarySource.Should().NotContain("video-secret");
        prepared.ExternalAudioSource.Should().NotContain("audio-secret");
    }

    [Fact]
    public void Read_UsesFiniteRangesAndRetainsHeadersAcrossSeeks()
    {
        var bytes = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var handler = new ByteRangeHandler(bytes);
        using var client = new HttpClient(handler);
        var source = new MpvHttpRangeSource(
            new Uri("https://video.googlevideo.com/videoplayback?clen=64"),
            bytes.Length,
            new Dictionary<string, string>
            {
                ["User-Agent"] = "Example/1.0, compatible",
            });
        using var stream = new MpvHttpRangeStream(client, source, chunkSize: 16);

        stream.Read(8).Should().Equal(bytes[..8]);
        stream.Seek(32).Should().Be(32);
        stream.Read(4).Should().Equal(bytes[32..36]);

        handler.Ranges.Should().Equal((0L, 15L), (32L, 47L));
        handler.UserAgents.Should().OnlyContain(value => value == "Example/1.0, compatible");
    }

    [Fact]
    public void SerializeHttpHeaders_EscapesListDelimiters()
    {
        MpvPlaybackEngine.SerializeHttpHeaders(new Dictionary<string, string>
            {
                ["User-Agent"] = "Mozilla/5.0, Example\\1",
                ["Referer"] = "https://www.youtube.com/",
            })
            .Should().Be("User-Agent: Mozilla/5.0\\, Example\\\\1,Referer: https://www.youtube.com/");
    }

    private sealed class ByteRangeHandler(byte[] content) : HttpMessageHandler
    {
        public List<(long Start, long End)> Ranges { get; } = [];
        public List<string> UserAgents { get; } = [];

        protected override HttpResponseMessage Send(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var range = request.Headers.Range?.Ranges.Single();
            range.Should().NotBeNull();
            range!.From.Should().HaveValue();
            range.To.Should().HaveValue("the bridge must never emit an open-ended Range request");
            var start = range.From!.Value;
            var end = Math.Min(range.To!.Value, content.LongLength - 1);
            Ranges.Add((start, end));
            UserAgents.Add(string.Join(", ", request.Headers.GetValues("User-Agent")));

            var body = content[(int)start..((int)end + 1)];
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(body),
                RequestMessage = request,
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, content.LongLength);
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }
}
