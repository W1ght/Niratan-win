using System;
using System.Text.Json;

namespace Hoshi.Views.Pages;

internal readonly record struct NovelReaderChapterReadyPayload(
    int ChapterIndex,
    long? NavigationGeneration,
    long RenderAttemptId);

internal readonly record struct NovelReaderRestoreCompletedPayload(
    int ChapterIndex,
    long? NavigationGeneration,
    long RenderAttemptId,
    double Progress);

internal static class NovelReaderTerminalPayloadParser
{
    public static bool TryParseChapterReady(
        JsonElement message,
        out NovelReaderChapterReadyPayload payload) =>
        TryParseChapterReady(message, out payload, out _);

    public static bool TryParseChapterReady(
        JsonElement message,
        out NovelReaderChapterReadyPayload payload,
        out JsonElement rawPayload)
    {
        payload = default;
        rawPayload = default;
        if (!TryGetPayload(message, out var body)
            || !TryGetChapterIndex(body, out var chapterIndex)
            || !body.TryGetProperty("navigationGeneration", out var generationElement)
            || !TryGetPositiveInt64(body, "renderAttemptId", out var renderAttemptId))
        {
            return false;
        }

        long? generation;
        if (generationElement.ValueKind == JsonValueKind.Null)
        {
            generation = null;
        }
        else if (generationElement.ValueKind == JsonValueKind.Number
            && generationElement.TryGetInt64(out var value)
            && value > 0)
        {
            generation = value;
        }
        else
        {
            return false;
        }

        payload = new NovelReaderChapterReadyPayload(
            chapterIndex,
            generation,
            renderAttemptId);
        rawPayload = body;
        return true;
    }

    public static bool TryParseRestoreCompleted(
        JsonElement message,
        out NovelReaderRestoreCompletedPayload payload)
    {
        payload = default;
        if (!TryGetPayload(message, out var body)
            || !TryGetChapterIndex(body, out var chapterIndex)
            || !body.TryGetProperty("navigationGeneration", out var generationElement)
            || !TryGetPositiveInt64(body, "renderAttemptId", out var renderAttemptId)
            || !body.TryGetProperty("progress", out var progressElement)
            || progressElement.ValueKind != JsonValueKind.Number
            || !progressElement.TryGetDouble(out var progress)
            || !double.IsFinite(progress)
            || progress is < 0 or > 1)
        {
            return false;
        }

        long? generation;
        if (generationElement.ValueKind == JsonValueKind.Null)
        {
            generation = null;
        }
        else if (generationElement.ValueKind == JsonValueKind.Number
            && generationElement.TryGetInt64(out var generationValue)
            && generationValue > 0)
        {
            generation = generationValue;
        }
        else
        {
            return false;
        }

        payload = new NovelReaderRestoreCompletedPayload(
            chapterIndex,
            generation,
            renderAttemptId,
            progress);
        return true;
    }

    private static bool TryGetPayload(JsonElement message, out JsonElement payload)
    {
        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("payload", out payload)
            && payload.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        payload = default;
        return false;
    }

    private static bool TryGetChapterIndex(JsonElement payload, out int chapterIndex)
    {
        chapterIndex = -1;
        return payload.TryGetProperty("chapterIndex", out var chapterElement)
            && chapterElement.ValueKind == JsonValueKind.Number
            && chapterElement.TryGetInt32(out chapterIndex)
            && chapterIndex >= 0;
    }

    private static bool TryGetPositiveInt64(
        JsonElement payload,
        string propertyName,
        out long value)
    {
        value = 0;
        return payload.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value > 0;
    }
}
