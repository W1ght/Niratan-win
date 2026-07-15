namespace Niratan.Services.Video;

public readonly record struct VideoViewportGeometry(
    double OsdWidth,
    double OsdHeight,
    double TopMargin,
    double BottomMargin,
    double LeftMargin,
    double RightMargin)
{
    public bool IsValid =>
        double.IsFinite(OsdWidth)
        && double.IsFinite(OsdHeight)
        && double.IsFinite(TopMargin)
        && double.IsFinite(BottomMargin)
        && double.IsFinite(LeftMargin)
        && double.IsFinite(RightMargin)
        && OsdWidth > 0
        && OsdHeight > 0
        && TopMargin >= 0
        && BottomMargin >= 0
        && LeftMargin >= 0
        && RightMargin >= 0
        && TopMargin + BottomMargin <= OsdHeight
        && LeftMargin + RightMargin <= OsdWidth;
}
