using FluentAssertions;
using Niratan.Models.Sasayaki;
using Niratan.Services.Sasayaki;
using Niratan.ViewModels.Components;

namespace Niratan.Tests.Services.Sasayaki;

public sealed class ReaderLyricsModeProjectionTests
{
    [Fact]
    public void CanEnter_RequiresEnabledAudioAndValidMatchData()
    {
        var matches = new SasayakiMatchData
        {
            Matches = [Cue("one", 10, 14)],
        };

        ReaderLyricsModeProjection.CanEnter(true, true, matches).Should().BeTrue();
        ReaderLyricsModeProjection.CanEnter(false, true, matches).Should().BeFalse();
        ReaderLyricsModeProjection.CanEnter(true, false, matches).Should().BeFalse();
        ReaderLyricsModeProjection.CanEnter(true, true, new SasayakiMatchData()).Should().BeFalse();
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(10, 0)]
    [InlineData(12, 0.5)]
    [InlineData(14, 1)]
    [InlineData(18, 1)]
    public void CueProgress_ClampsPlaybackToCue(double playback, double expected)
    {
        ReaderLyricsModeProjection.CueProgress(Cue("one", 10, 14), playback)
            .Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void VisibleCueWindow_UsesLessContextForShortReaders()
    {
        ReaderLyricsModeProjection.VisibleCueWindow(20, 10, 500)
            .Should().Be(new ReaderLyricsCueWindow(9, 12));
        ReaderLyricsModeProjection.VisibleCueWindow(20, 10, 1000)
            .Should().Be(new ReaderLyricsCueWindow(6, 15));
    }

    [Fact]
    public void ViewModel_MaskOnlyBlursDuringUnobstructedPlayback()
    {
        var viewModel = new ReaderLyricsViewModel
        {
            IsMaskEnabled = true,
            IsPlaying = true,
        };

        viewModel.ShouldBlurLyrics.Should().BeTrue();
        viewModel.IsPointerOverLyrics = true;
        viewModel.ShouldBlurLyrics.Should().BeFalse();
        viewModel.IsPointerOverLyrics = false;
        viewModel.IsLookupPopupVisible = true;
        viewModel.ShouldBlurLyrics.Should().BeFalse();
    }

    [Fact]
    public void ViewModel_TracksCurrentCueAndPlaybackProgress()
    {
        var cues = new[]
        {
            Cue("one", 10, 14),
            Cue("two", 14, 18),
        };
        var viewModel = new ReaderLyricsViewModel();

        viewModel.Configure("Book", null, cues, 1, 0);
        viewModel.UpdatePlayback(true, 16, 20, 1);

        viewModel.CurrentCueText.Should().Be("two");
        viewModel.CurrentCueProgress.Should().BeApproximately(0.5, 0.0001);
        viewModel.PlaybackProgress.Should().BeApproximately(0.8, 0.0001);
    }

    private static SasayakiMatch Cue(string text, double start, double end) => new()
    {
        Id = text,
        Text = text,
        StartTime = start,
        EndTime = end,
        ChapterIndex = 0,
        Start = (int)start,
        Length = text.Length,
    };
}
