using System.Text.Json;

namespace Niratan.Views.Pages;

internal readonly record struct NovelReaderBlankClickPayload(
    double X,
    double Y,
    double ViewportWidth,
    double ViewportHeight);

internal static class NovelReaderInteractionPayloadParser
{
    public static bool TryParseReaderBlankClick(
        JsonElement message,
        out NovelReaderBlankClickPayload payload)
    {
        payload = default;
        if (message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("payload", out var body)
            || body.ValueKind != JsonValueKind.Object
            || !TryGetFiniteDouble(body, "x", out var x)
            || !TryGetFiniteDouble(body, "y", out var y)
            || !TryGetFiniteDouble(body, "viewportWidth", out var viewportWidth)
            || !TryGetFiniteDouble(body, "viewportHeight", out var viewportHeight)
            || viewportWidth <= 0
            || viewportHeight <= 0
            || x < 0
            || y < 0
            || x > viewportWidth
            || y > viewportHeight)
        {
            return false;
        }

        payload = new NovelReaderBlankClickPayload(x, y, viewportWidth, viewportHeight);
        return true;
    }

    private static bool TryGetFiniteDouble(
        JsonElement body,
        string propertyName,
        out double value)
    {
        value = 0;
        return body.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value)
            && double.IsFinite(value);
    }
}
