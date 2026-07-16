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

public readonly record struct VideoDisplayInfo(
    int Width,
    int Height,
    int RotationDegrees)
{
    public bool IsValid => Width > 0 && Height > 0;

    public double? AspectRatio
    {
        get
        {
            if (!IsValid)
                return null;

            var ratio = (double)Width / Height;
            var rotation = ((RotationDegrees % 360) + 360) % 360;
            return rotation is 90 or 270 ? 1 / ratio : ratio;
        }
    }
}
