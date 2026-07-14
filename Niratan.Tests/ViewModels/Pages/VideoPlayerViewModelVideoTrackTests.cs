using System.Collections;
using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Services.Dictionary;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public class VideoPlayerViewModelVideoTrackTests
{
    [Fact]
    public void ReplaceTracks_ListsVideoTracksAndKeepsSelectedTrack()
    {
        var sut = CreateSut();
        var type = sut.GetType();
        var videoTracksProperty = type.GetProperty("VideoTracks");
        var hasVideoTracksProperty = type.GetProperty("HasVideoTracks");
        var selectedVideoTrackIdProperty = type.GetProperty("SelectedVideoTrackId");

        videoTracksProperty.Should().NotBeNull();
        hasVideoTracksProperty.Should().NotBeNull();
        selectedVideoTrackIdProperty.Should().NotBeNull();

        sut.ReplaceTracks(
        [
            new VideoTrackInfo(7, VideoTrackType.Audio, "Japanese", "jpn", "aac", 2, null, false, true),
            new VideoTrackInfo(3, VideoTrackType.Video, "Alternate Angle", null, "h265", 1, null, false, false),
            new VideoTrackInfo(1, VideoTrackType.Video, "Main Video", null, "h264", 0, null, false, true),
            new VideoTrackInfo(8, VideoTrackType.Subtitle, "Japanese", "jpn", "subrip", 3, null, false, true),
        ]);

        Get<bool>(sut, "HasVideoTracks").Should().BeTrue();
        Get<int?>(sut, "SelectedVideoTrackId").Should().Be(1);

        var videoTracks = GetCollection<VideoTrackInfo>(sut, "VideoTracks");
        videoTracks.Select(track => track.Id).Should().Equal(1, 3);
        videoTracks[0].DisplayName.Should().Be("Main Video");
    }

    [Fact]
    public void SelectVideoTrack_UpdatesSelectedTrackAndStatus()
    {
        var sut = CreateSut();
        var method = sut.GetType().GetMethod("SelectVideoTrack", [typeof(VideoTrackInfo)]);
        method.Should().NotBeNull();

        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Video,
            "Alternate Angle",
            null,
            "h265",
            1,
            null,
            false,
            false);

        method!.Invoke(sut, [track]);

        Get<int?>(sut, "SelectedVideoTrackId").Should().Be(3);
        sut.StatusText.Should().Be("Video track: Alternate Angle");
    }

    private static T Get<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"property {propertyName} should exist");
        var value = property!.GetValue(instance);
        if (value == null)
            return default!;

        return value.Should().BeAssignableTo<T>().Subject;
    }

    private static List<T> GetCollection<T>(object instance, string propertyName)
    {
        var value = Get<object>(instance, propertyName);
        value.Should().BeAssignableTo<IEnumerable>();
        return ((IEnumerable)value).Cast<T>().ToList();
    }

    private static VideoPlayerViewModel CreateSut()
    {
        return new VideoPlayerViewModel(
            new SubtitleParserService(),
            Mock.Of<IDictionaryPopupRequestService>());
    }
}
