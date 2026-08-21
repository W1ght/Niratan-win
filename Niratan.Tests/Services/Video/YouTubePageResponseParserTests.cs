using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.Tests.Services.Video;

public sealed class YouTubePageResponseParserTests
{
    [Fact]
    public void ParseJson_ExtractsPlayableStreamsAndAutomaticCaptions()
    {
        const string json = """
        {
          "videoDetails": {
            "title": "Test video",
            "lengthSeconds": "1474",
            "thumbnail": { "thumbnails": [{ "url": "https://i.ytimg.com/vi/test/default.jpg", "width": 120, "height": 90 }] }
          },
          "streamingData": {
            "formats": [{
              "itag": 18,
              "url": "https://rr1---sn.example.googlevideo.com/videoplayback?itag=18&expire=1900007200",
              "mimeType": "video/mp4; codecs=\"avc1.42001E, mp4a.40.2\"",
              "height": 360,
              "bitrate": 500000
            }],
            "adaptiveFormats": [{
              "itag": 137,
              "url": "https://rr1---sn.example.googlevideo.com/videoplayback?itag=137&expire=1900007200",
              "mimeType": "video/mp4; codecs=\"avc1.640028\"",
              "height": 1080,
              "averageBitrate": 2000000
            }]
          },
          "captions": {
            "playerCaptionsTracklistRenderer": {
              "captionTracks": [{
                "baseUrl": "/api/timedtext?v=test&kind=asr&lang=ja",
                "name": { "simpleText": "Japanese (auto-generated)" },
                "vssId": "a.ja",
                "languageCode": "ja",
                "kind": "asr"
              }]
            }
          }
        }
        """;

        var response = YouTubePlayerResponseParser.ParseJson(json);

        response.Title.Should().Be("Test video");
        response.Duration.Should().Be(TimeSpan.FromSeconds(1474));
        response.Streams.Should().HaveCount(2);
        response.Streams.Should().Contain(stream => stream.HasVideo && stream.HasAudio);
        response.Streams.Should().Contain(stream => stream.Height == 1080 && !stream.HasAudio);
        response.SubtitleOptions.Should().ContainSingle();
        response.SubtitleOptions[0].IsAutomatic.Should().BeTrue();
        response.SubtitleOptions[0].Language.Should().Be("ja");
        response.SubtitleOptions[0].SourceUrl.Should().Contain("fmt=vtt");
    }

    [Fact]
    public async Task DownloadSubtitleAsync_ConvertsYouTubeTimedTextXmlToSrt()
    {
        const string xml = """
        <?xml version="1.0" encoding="utf-8" ?>
        <timedtext><body>
          <text t="0" d="1500">こんにちは &amp; 世界</text>
        </body></timedtext>
        """;
        using var httpClient = new HttpClient(new StaticResponseHandler(xml));
        using var loader = new YouTubePageResponseLoader(httpClient);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"niratan-youtube-subtitle-{Guid.NewGuid():N}.srt");

        try
        {
            await loader.DownloadSubtitleAsync(
                new RemoteVideoSubtitleOption(
                    "a.ja",
                    "ja",
                    "Japanese (auto-generated)",
                    "https://www.youtube.com/api/timedtext?v=test&fmt=vtt",
                    true),
                outputPath,
                TestContext.Current.CancellationToken);

            var text = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
            text.Should().Contain("00:00:00,000 --> 00:00:01,500");
            text.Should().Contain("こんにちは & 世界");
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    private sealed class StaticResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request,
            });
    }
}
