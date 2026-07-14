using FluentAssertions;
using Moq;
using Niratan.Models;
using Niratan.Services.Dictionary;
using Niratan.Services.Video;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public class VideoPlayerViewModelEmbeddedSubtitleTests
{
    [Fact]
    public void ReplaceTracks_ListsSubtitleTracksAndKeepsSelectedEmbeddedTrack()
    {
        var sut = CreateSut();

        sut.ReplaceTracks(
        [
            new VideoTrackInfo(1, VideoTrackType.Video, "Video", null, "h264", 0, null, false, true),
            new VideoTrackInfo(2, VideoTrackType.Audio, "Audio", "jpn", "aac", 1, null, false, true),
            new VideoTrackInfo(3, VideoTrackType.Subtitle, "Japanese", "jpn", "subrip", 2, null, false, true),
            new VideoTrackInfo(4, VideoTrackType.Subtitle, "Signs", "eng", "ass", 3, null, false, false),
        ]);

        sut.HasSubtitleTracks.Should().BeTrue();
        sut.HasAudioTracks.Should().BeTrue();
        sut.AudioTracks.Should().ContainSingle();
        sut.AudioTracks[0].Id.Should().Be(2);
        sut.SelectedAudioTrackId.Should().Be(2);
        sut.SubtitleTracks.Should().HaveCount(2);
        sut.SubtitleTracks.Select(track => track.Id).Should().Equal(3, 4);
        sut.SelectedSubtitleTrackId.Should().Be(3);
        sut.EmbeddedSubtitleName.Should().Be("Japanese · jpn");
        sut.IsEmbeddedSubtitleActive.Should().BeTrue();
    }

    [Fact]
    public void UpdatePosition_WhenEmbeddedTrackIsActive_KeepsCurrentMpvSubtitleCue()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var previousCue = new VideoSubtitleCue(
            0,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            "前の内封字幕");
        var cue = new VideoSubtitleCue(
            0,
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            "内封字幕");

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.UpdateEmbeddedSubtitleCue(previousCue);
        sut.UpdateEmbeddedSubtitleCue(cue);
        sut.UpdatePosition(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));

        sut.CurrentSubtitleText.Should().Be("内封字幕");
        sut.HasCurrentSubtitle.Should().BeTrue();
        sut.GetPreviousSubtitleStart().Should().Be(TimeSpan.FromSeconds(1));
        sut.GetNextSubtitleStart().Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpdatePosition_WithExternalSubtitles_MarksTranscriptCurrentRow()
    {
        var sut = CreateSut();
        var subtitlePath = Path.Combine(
            Path.GetTempPath(),
            $"niratan-video-transcript-{Guid.NewGuid():N}.srt");

        try
        {
            await File.WriteAllTextAsync(
                subtitlePath,
                """
                1
                00:00:01,000 --> 00:00:02,000
                最初の字幕

                2
                00:00:03,000 --> 00:00:04,000
                次の字幕
                """,
                TestContext.Current.CancellationToken);

            await sut.LoadSubtitleAsync(subtitlePath, TestContext.Current.CancellationToken);

            sut.HasTranscriptRows.Should().BeTrue();
            sut.TranscriptRows.Select(row => row.Text).Should().Equal("最初の字幕", "次の字幕");

            sut.UpdatePosition(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5));
            sut.TranscriptRows[0].IsCurrent.Should().BeTrue();
            sut.TranscriptRows[1].IsCurrent.Should().BeFalse();

            sut.UpdatePosition(TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(5));
            sut.TranscriptRows[0].IsCurrent.Should().BeFalse();
            sut.TranscriptRows[1].IsCurrent.Should().BeTrue();

            sut.UpdatePosition(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(5));
            sut.TranscriptRows.Should().OnlyContain(row => !row.IsCurrent);
        }
        finally
        {
            if (File.Exists(subtitlePath))
                File.Delete(subtitlePath);
        }
    }

    [Fact]
    public async Task UpdatePosition_WithOverlappingExternalSubtitles_DisplaysAndMarksAllCurrentRows()
    {
        var sut = CreateSut();
        var subtitlePath = Path.Combine(
            Path.GetTempPath(),
            $"niratan-video-overlap-{Guid.NewGuid():N}.srt");

        try
        {
            await File.WriteAllTextAsync(
                subtitlePath,
                """
                1
                00:00:00,000 --> 00:00:10,000
                長い字幕

                2
                00:00:03,000 --> 00:00:04,000
                重なる字幕
                """,
                TestContext.Current.CancellationToken);

            await sut.LoadSubtitleAsync(subtitlePath, TestContext.Current.CancellationToken);

            sut.UpdatePosition(TimeSpan.FromSeconds(3.5), TimeSpan.FromSeconds(12));

            sut.CurrentSubtitleText.Should().Be("長い字幕\n重なる字幕");
            sut.HasCurrentSubtitle.Should().BeTrue();
            sut.TranscriptRows.Select(row => row.IsCurrent).Should().Equal(true, true);

            sut.UpdatePosition(TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(12));

            sut.CurrentSubtitleText.Should().BeEmpty();
            sut.TranscriptRows.Should().OnlyContain(row => !row.IsCurrent);
        }
        finally
        {
            if (File.Exists(subtitlePath))
                File.Delete(subtitlePath);
        }
    }

    [Fact]
    public async Task LoadVideoAsync_WithMalformedSidecar_KeepsVideoLoadedAndReportsSubtitleError()
    {
        var sut = CreateSut();
        var subtitlePath = Path.Combine(
            Path.GetTempPath(),
            $"niratan-video-bad-subtitle-{Guid.NewGuid():N}.srt");
        var video = new VideoItem
        {
            Title = "bad subtitle sample",
            FilePath = @"D:\Videos\bad-subtitle-sample.mp4",
            SubtitlePath = subtitlePath,
        };

        try
        {
            await File.WriteAllTextAsync(
                subtitlePath,
                "not a subtitle",
                TestContext.Current.CancellationToken);

            await sut.LoadVideoAsync(video, TestContext.Current.CancellationToken);

            sut.CurrentVideo.Should().BeSameAs(video);
            sut.PrimarySubtitleName.Should().BeEmpty();
            sut.HasTranscriptRows.Should().BeFalse();
            sut.StatusText.Should().Contain("Failed to load subtitles");
        }
        finally
        {
            if (File.Exists(subtitlePath))
                File.Delete(subtitlePath);
        }
    }

    [Fact]
    public void UpdateEmbeddedSubtitleCue_AddsTranscriptRowsInTimeOrderAndDeduplicates()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var laterCue = new VideoSubtitleCue(
            0,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(9),
            "後の字幕");
        var earlierCue = new VideoSubtitleCue(
            1,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(3),
            "前の字幕");

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.UpdateEmbeddedSubtitleCue(laterCue);
        sut.UpdateEmbeddedSubtitleCue(earlierCue);
        sut.UpdateEmbeddedSubtitleCue(earlierCue);

        sut.HasTranscriptRows.Should().BeTrue();
        sut.TranscriptRows.Should().HaveCount(2);
        sut.TranscriptRows.Select(row => row.Text).Should().Equal("前の字幕", "後の字幕");
        sut.TranscriptRows[0].IsCurrent.Should().BeTrue();
        sut.TranscriptRows[1].IsCurrent.Should().BeFalse();
    }

    [Fact]
    public void ReplaceEmbeddedSubtitleCues_LoadsFullTranscriptAndLetsPositionDriveCurrentRow()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);

        sut.SelectEmbeddedSubtitleTrack(track);
        var replaced = sut.ReplaceEmbeddedSubtitleCues(
            track,
            [
                new VideoSubtitleCue(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "最初の字幕"),
                new VideoSubtitleCue(1, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), "次の字幕"),
                new VideoSubtitleCue(2, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8), "最後の字幕"),
            ]);

        replaced.Should().BeTrue();
        sut.HasTranscriptRows.Should().BeTrue();
        sut.TranscriptRows.Select(row => row.Text).Should().Equal("最初の字幕", "次の字幕", "最後の字幕");

        sut.UpdatePosition(TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(10));

        sut.CurrentSubtitleText.Should().Be("次の字幕");
        sut.HasCurrentSubtitle.Should().BeTrue();
        sut.TranscriptRows.Select(row => row.IsCurrent).Should().Equal(false, true, false);
        sut.GetPreviousSubtitleStart().Should().Be(TimeSpan.FromSeconds(1));
        sut.GetNextSubtitleStart().Should().Be(TimeSpan.FromSeconds(7));

        sut.UpdateEmbeddedSubtitleCue(new VideoSubtitleCue(
            0,
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(5),
            "次の字幕"));

        sut.TranscriptRows.Should().HaveCount(3);
        sut.TranscriptRows.Select(row => row.IsCurrent).Should().Equal(false, true, false);
    }

    [Fact]
    public void ReplaceEmbeddedSubtitleCues_IgnoresStaleTrackResults()
    {
        var sut = CreateSut();
        var selected = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var stale = selected with { Id = 4, Title = "English" };

        sut.SelectEmbeddedSubtitleTrack(selected);
        var replaced = sut.ReplaceEmbeddedSubtitleCues(
            stale,
            [new VideoSubtitleCue(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "旧轨道")]);

        replaced.Should().BeFalse();
        sut.HasTranscriptRows.Should().BeFalse();
        sut.TranscriptRows.Should().BeEmpty();
        sut.SelectedSubtitleTrackId.Should().Be(3);
    }

    [Fact]
    public void ReplaceEmbeddedSubtitleCues_DoesNotRestoreHiddenSubtitles()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.ToggleSubtitlesVisible();

        var replaced = sut.ReplaceEmbeddedSubtitleCues(
            track,
            [new VideoSubtitleCue(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "隠れた字幕")]);

        replaced.Should().BeTrue();
        sut.AreSubtitlesVisible.Should().BeFalse();
        sut.HasTranscriptRows.Should().BeTrue();
        sut.HasCurrentSubtitle.Should().BeFalse();
        sut.CurrentSubtitleText.Should().BeEmpty();
    }

    [Fact]
    public void ReplaceEmbeddedSubtitleCues_KeepsFullTranscriptButWindowsVisibleRows()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var cues = Enumerable
            .Range(0, 140)
            .Select(index => new VideoSubtitleCue(
                index,
                TimeSpan.FromSeconds(index * 2),
                TimeSpan.FromSeconds(index * 2 + 1),
                $"字幕 {index:000}"))
            .ToList();

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.ReplaceEmbeddedSubtitleCues(track, cues);

        sut.TranscriptRows.Should().HaveCount(140);
        sut.TranscriptVisibleRows.Should().HaveCount(80);
        sut.TranscriptVisibleRows[0].Text.Should().Be("字幕 000");
        sut.TranscriptVisibleRows[^1].Text.Should().Be("字幕 079");

        sut.UpdatePosition(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300));

        sut.TranscriptRows.Should().HaveCount(140);
        sut.TranscriptVisibleRows.Should().HaveCount(80);
        sut.TranscriptVisibleRows.Should().Contain(row => row.Text == "字幕 100" && row.IsCurrent);
        sut.TranscriptVisibleRows[0].Index.Should().BeGreaterThan(0);
        sut.TranscriptVisibleRows[^1].Index.Should().BeLessThan(140);
    }

    [Fact]
    public void UpdatePosition_WhenAdjacentCurrentRowStaysInsideVisibleWindow_DoesNotRebuildVisibleRows()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var cues = Enumerable
            .Range(0, 140)
            .Select(index => new VideoSubtitleCue(
                index,
                TimeSpan.FromSeconds(index * 2),
                TimeSpan.FromSeconds(index * 2 + 1),
                $"字幕 {index:000}"))
            .ToList();

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.ReplaceEmbeddedSubtitleCues(track, cues);
        sut.UpdatePosition(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300));

        sut.TranscriptVisibleRows[0].Index.Should().Be(60);
        sut.TranscriptVisibleRows[^1].Index.Should().Be(139);

        var visibleRowsChanged = 0;
        sut.TranscriptVisibleRows.CollectionChanged += (_, _) => visibleRowsChanged++;

        sut.UpdatePosition(TimeSpan.FromSeconds(202), TimeSpan.FromSeconds(300));

        visibleRowsChanged.Should().Be(0);
        sut.TranscriptVisibleRows[0].Index.Should().Be(60);
        sut.TranscriptVisibleRows[^1].Index.Should().Be(139);
        sut.TranscriptVisibleRows.Should().Contain(row => row.Text == "字幕 101" && row.IsCurrent);
    }

    [Fact]
    public void UpdatePosition_WhenPlaybackCrossesSubtitleGapToAdjacentRow_DoesNotRebuildVisibleRows()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var cues = Enumerable
            .Range(0, 140)
            .Select(index => new VideoSubtitleCue(
                index,
                TimeSpan.FromSeconds(index * 2),
                TimeSpan.FromSeconds(index * 2 + 1),
                $"字幕 {index:000}"))
            .ToList();

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.ReplaceEmbeddedSubtitleCues(track, cues);
        sut.UpdatePosition(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300));
        sut.UpdatePosition(TimeSpan.FromSeconds(201.5), TimeSpan.FromSeconds(300));

        sut.TranscriptRows.Should().OnlyContain(row => !row.IsCurrent);
        sut.TranscriptVisibleRows[0].Index.Should().Be(60);
        sut.TranscriptVisibleRows[^1].Index.Should().Be(139);

        var visibleRowsChanged = 0;
        sut.TranscriptVisibleRows.CollectionChanged += (_, _) => visibleRowsChanged++;

        sut.UpdatePosition(TimeSpan.FromSeconds(202), TimeSpan.FromSeconds(300));

        visibleRowsChanged.Should().Be(0);
        sut.TranscriptVisibleRows[0].Index.Should().Be(60);
        sut.TranscriptVisibleRows[^1].Index.Should().Be(139);
        sut.TranscriptVisibleRows.Should().Contain(row => row.Text == "字幕 101" && row.IsCurrent);
    }

    [Fact]
    public void TranscriptWindow_ExpandsAtEdgesWithoutLosingFullTranscript()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var cues = Enumerable
            .Range(0, 140)
            .Select(index => new VideoSubtitleCue(
                index,
                TimeSpan.FromSeconds(index * 2),
                TimeSpan.FromSeconds(index * 2 + 1),
                $"字幕 {index:000}"))
            .ToList();

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.ReplaceEmbeddedSubtitleCues(track, cues);

        sut.ExpandTranscriptWindowTowardEnd();
        sut.TranscriptVisibleRows.Should().HaveCount(120);
        sut.TranscriptVisibleRows[^1].Text.Should().Be("字幕 119");

        sut.UpdatePosition(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(300));
        var startAfterJump = sut.TranscriptVisibleRows[0].Index;

        sut.ExpandTranscriptWindowTowardStart();

        sut.TranscriptRows.Should().HaveCount(140);
        sut.TranscriptVisibleRows[0].Index.Should().BeLessThan(startAfterJump);
        sut.TranscriptVisibleRows.Should().Contain(row => row.Text == "字幕 100" && row.IsCurrent);
    }

    [Fact]
    public async Task ToggleSubtitlesVisible_HidesAndRestoresExternalSubtitleWithoutClearingIt()
    {
        var sut = CreateSut();
        var subtitlePath = Path.Combine(
            Path.GetTempPath(),
            $"niratan-video-visibility-{Guid.NewGuid():N}.srt");

        try
        {
            await File.WriteAllTextAsync(
                subtitlePath,
                """
                1
                00:00:01,000 --> 00:00:02,000
                表示される字幕
                """,
                TestContext.Current.CancellationToken);

            await sut.LoadSubtitleAsync(subtitlePath, TestContext.Current.CancellationToken);
            sut.UpdatePosition(TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(5));

            sut.AreSubtitlesVisible.Should().BeTrue();
            sut.CurrentSubtitleText.Should().Be("表示される字幕");
            sut.HasCurrentSubtitle.Should().BeTrue();

            sut.ToggleSubtitlesVisible();

            sut.AreSubtitlesVisible.Should().BeFalse();
            sut.PrimarySubtitleName.Should().Be(Path.GetFileName(subtitlePath));
            sut.CurrentSubtitleText.Should().Be("");
            sut.HasCurrentSubtitle.Should().BeFalse();
            sut.SubtitlePanelHeight.Should().Be(0);

            sut.ToggleSubtitlesVisible();

            sut.AreSubtitlesVisible.Should().BeTrue();
            sut.PrimarySubtitleName.Should().Be(Path.GetFileName(subtitlePath));
            sut.CurrentSubtitleText.Should().Be("表示される字幕");
            sut.HasCurrentSubtitle.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(subtitlePath))
                File.Delete(subtitlePath);
        }
    }

    [Fact]
    public void ToggleSubtitlesVisible_HidesAndRestoresEmbeddedSubtitleWithoutDroppingTrack()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            true);
        var cue = new VideoSubtitleCue(
            0,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            "内封字幕");

        sut.SelectEmbeddedSubtitleTrack(track);
        sut.UpdateEmbeddedSubtitleCue(cue);

        sut.CurrentSubtitleText.Should().Be("内封字幕");

        sut.ToggleSubtitlesVisible();

        sut.AreSubtitlesVisible.Should().BeFalse();
        sut.SelectedSubtitleTrackId.Should().Be(3);
        sut.CurrentSubtitleText.Should().Be("");
        sut.HasCurrentSubtitle.Should().BeFalse();

        sut.ToggleSubtitlesVisible();

        sut.AreSubtitlesVisible.Should().BeTrue();
        sut.SelectedSubtitleTrackId.Should().Be(3);
        sut.CurrentSubtitleText.Should().Be("内封字幕");
        sut.HasCurrentSubtitle.Should().BeTrue();
    }

    [Fact]
    public void GetNextSubtitleTrackForCycle_FollowsNiratanSubtitleTrackOrder()
    {
        var sut = CreateSut();
        var first = new VideoTrackInfo(
            3,
            VideoTrackType.Subtitle,
            "Japanese",
            "jpn",
            "subrip",
            2,
            null,
            false,
            false);
        var second = new VideoTrackInfo(
            4,
            VideoTrackType.Subtitle,
            "Signs",
            "eng",
            "ass",
            3,
            null,
            false,
            false);

        sut.ReplaceTracks(
        [
            first,
            second,
        ]);

        sut.GetNextSubtitleTrackForCycle().Should().Be(first);

        sut.SelectEmbeddedSubtitleTrack(first);
        sut.GetNextSubtitleTrackForCycle().Should().Be(second);

        sut.SelectEmbeddedSubtitleTrack(second);
        sut.GetNextSubtitleTrackForCycle().Should().BeNull();

        sut.ToggleSubtitlesVisible();
        sut.GetNextSubtitleTrackForCycle().Should().Be(second);
    }

    [Fact]
    public void SelectAudioTrack_UpdatesSelectedAudioTrackAndStatus()
    {
        var sut = CreateSut();
        var track = new VideoTrackInfo(
            2,
            VideoTrackType.Audio,
            "Japanese",
            "jpn",
            "aac",
            1,
            null,
            false,
            true);

        sut.SelectAudioTrack(track);

        sut.SelectedAudioTrackId.Should().Be(2);
        sut.StatusText.Should().Be("Audio track: Japanese · jpn");

        sut.SelectAudioTrack(null);

        sut.SelectedAudioTrackId.Should().BeNull();
        sut.StatusText.Should().Be("Audio off");
    }

    private static VideoPlayerViewModel CreateSut()
    {
        return new VideoPlayerViewModel(
            new SubtitleParserService(),
            Mock.Of<IDictionaryPopupRequestService>());
    }
}
