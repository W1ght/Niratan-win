using FluentAssertions;
using Niratan.Services.Novels;

namespace Niratan.Tests.Services.Novels;

public sealed class ReaderGalleryProgressPolicyTests
{
    [Theory]
    [InlineData(2, 0.4, 1, 0.9, true)]
    [InlineData(2, 0.4, 2, 0.4, true)]
    [InlineData(2, 0.4, 2, 0.7, false)]
    [InlineData(2, 0.4, 3, 0.0, false)]
    [InlineData(0, 0.0, -1, 0.0, true)]
    public void IsRead_UsesChapterAndInChapterProgress(
        int currentChapter,
        double currentProgress,
        int imageChapter,
        double imageProgress,
        bool expected)
    {
        ReaderGalleryProgressPolicy.IsRead(
                currentChapter,
                currentProgress,
                imageChapter,
                imageProgress)
            .Should().Be(expected);
    }
}
