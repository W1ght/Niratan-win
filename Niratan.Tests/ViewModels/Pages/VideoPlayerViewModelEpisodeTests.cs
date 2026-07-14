using System.Collections;
using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Services.Dictionary;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public class VideoPlayerViewModelEpisodeTests
{
    [Fact]
    public void ReplaceEpisodes_BuildsNiratanStyleEpisodeRowsAndMarksCurrent()
    {
        var sut = CreateSut();
        var type = sut.GetType();
        var replaceEpisodes = type.GetMethod("ReplaceEpisodes", [typeof(IEnumerable<VideoItem>), typeof(VideoItem)]);
        var rowsProperty = type.GetProperty("EpisodeRows");
        var hasEpisodesProperty = type.GetProperty("HasEpisodes");

        replaceEpisodes.Should().NotBeNull();
        rowsProperty.Should().NotBeNull();
        hasEpisodesProperty.Should().NotBeNull();

        var episode1 = CreateVideo("episode-01", @"D:\Videos\episode-01.mkv");
        var episode2 = CreateVideo("episode-02", @"D:\Videos\episode-02.mkv");
        var episode3 = CreateVideo("episode-03", @"D:\Videos\episode-03.mkv");

        replaceEpisodes!.Invoke(sut, [new[] { episode1, episode2, episode3 }, episode2]);

        Get<bool>(sut, "HasEpisodes").Should().BeTrue();
        var rows = GetCollection<object>(sut, "EpisodeRows");
        rows.Select(row => Get<string>(row, "Title")).Should().Equal("episode-01", "episode-02", "episode-03");
        rows.Select(row => Get<bool>(row, "IsCurrent")).Should().Equal(false, true, false);
        rows[1].Should().BeEquivalentTo(new
        {
            FilePath = episode2.FilePath,
            AutomationName = "episode-02, current episode",
        });
    }

    [Fact]
    public async Task LoadVideoAsync_SeedsSingleEpisodeWhenPlaylistIsNotProvided()
    {
        var sut = CreateSut();
        var video = CreateVideo("solo", @"D:\Videos\solo.mkv");

        await sut.LoadVideoAsync(video, TestContext.Current.CancellationToken);

        Get<bool>(sut, "HasEpisodes").Should().BeTrue();
        var row = GetCollection<object>(sut, "EpisodeRows").Single();
        Get<string>(row, "Title").Should().Be("solo");
        Get<bool>(row, "IsCurrent").Should().BeTrue();
    }

    private static VideoItem CreateVideo(string title, string path) =>
        new()
        {
            Id = title,
            Title = title,
            FilePath = path,
        };

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
