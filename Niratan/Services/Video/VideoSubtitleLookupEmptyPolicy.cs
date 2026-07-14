using System.Text.Json;

namespace Niratan.Services.Video;

internal readonly record struct VideoSubtitleLookupEmptyPolicy(
    bool DismissOnEmpty,
    bool IsHover)
{
    public static VideoSubtitleLookupEmptyPolicy FromCanvasLookup(bool isHoverLookup) =>
        new(!isHoverLookup, isHoverLookup);

    public static VideoSubtitleLookupEmptyPolicy FromWebPayload(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return default;

        var dismissOnEmpty = payload.TryGetProperty(
                "dismissOnEmpty",
                out var dismissElement)
            && dismissElement.ValueKind is JsonValueKind.True;
        var isHover = payload.TryGetProperty("isHover", out var hoverElement)
            && hoverElement.ValueKind is JsonValueKind.True;
        return new VideoSubtitleLookupEmptyPolicy(dismissOnEmpty, isHover);
    }
}
