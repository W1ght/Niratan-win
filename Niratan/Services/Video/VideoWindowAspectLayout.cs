using System;

namespace Niratan.Services.Video;

public readonly record struct VideoLayoutSize(double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(Width)
        && double.IsFinite(Height)
        && Width > 0
        && Height > 0;
}

public static class VideoWindowAspectLayout
{
    public static VideoLayoutSize FitContentSize(
        VideoLayoutSize currentContentSize,
        double videoAspectRatio,
        double sidebarWidth,
        VideoLayoutSize maximumContentSize)
    {
        if (!currentContentSize.IsValid
            || !maximumContentSize.IsValid
            || !double.IsFinite(videoAspectRatio)
            || videoAspectRatio <= 0)
        {
            return currentContentSize;
        }

        sidebarWidth = Math.Max(sidebarWidth, 0);
        var maximumVideoWidth = Math.Max(maximumContentSize.Width - sidebarWidth, 1);
        var height = Math.Min(currentContentSize.Height, maximumContentSize.Height);
        if (height * videoAspectRatio > maximumVideoWidth)
            height = Math.Max(maximumVideoWidth / videoAspectRatio, 1);

        return new VideoLayoutSize(
            Math.Min((height * videoAspectRatio) + sidebarWidth, maximumContentSize.Width),
            height);
    }

    public static VideoLayoutSize ConstrainFrameSize(
        VideoLayoutSize currentFrameSize,
        VideoLayoutSize proposedFrameSize,
        VideoLayoutSize frameDecorationSize,
        double videoAspectRatio,
        double sidebarWidth,
        VideoLayoutSize minimumFrameSize)
    {
        if (!currentFrameSize.IsValid
            || !proposedFrameSize.IsValid
            || !minimumFrameSize.IsValid
            || !double.IsFinite(frameDecorationSize.Width)
            || !double.IsFinite(frameDecorationSize.Height)
            || !double.IsFinite(videoAspectRatio)
            || videoAspectRatio <= 0)
        {
            return proposedFrameSize;
        }

        var decorationWidth = Math.Max(frameDecorationSize.Width, 0);
        var decorationHeight = Math.Max(frameDecorationSize.Height, 0);
        sidebarWidth = Math.Max(sidebarWidth, 0);
        var currentContentWidth = Math.Max(currentFrameSize.Width - decorationWidth, 1);
        var currentContentHeight = Math.Max(currentFrameSize.Height - decorationHeight, 1);
        var proposedContentWidth = Math.Max(proposedFrameSize.Width - decorationWidth, 1);
        var proposedContentHeight = Math.Max(proposedFrameSize.Height - decorationHeight, 1);
        var widthDelta = proposedContentWidth - currentContentWidth;
        var heightDelta = proposedContentHeight - currentContentHeight;
        const double tolerance = 0.5;

        if (Math.Abs(widthDelta) <= tolerance && Math.Abs(heightDelta) <= tolerance)
            return proposedFrameSize;

        var isWidthDriven = Math.Abs(heightDelta) <= tolerance
            || (Math.Abs(widthDelta) > tolerance
                && Math.Abs(widthDelta / videoAspectRatio) >= Math.Abs(heightDelta));
        var contentHeight = isWidthDriven
            ? (proposedContentWidth - sidebarWidth) / videoAspectRatio
            : proposedContentHeight;
        var minimumContentHeight = Math.Max(
            Math.Max(
                minimumFrameSize.Height - decorationHeight,
                (minimumFrameSize.Width - decorationWidth - sidebarWidth) / videoAspectRatio),
            1);
        contentHeight = Math.Max(contentHeight, minimumContentHeight);

        var result = new VideoLayoutSize(
            (contentHeight * videoAspectRatio) + sidebarWidth + decorationWidth,
            contentHeight + decorationHeight);
        return result.IsValid ? result : proposedFrameSize;
    }
}
