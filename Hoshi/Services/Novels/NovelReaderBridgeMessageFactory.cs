using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hoshi.Models.DTO;
using Hoshi.Models.Novel;

namespace Hoshi.Services.Novels;

public static class NovelReaderBridgeMessageFactory
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public static string CreateSetChapterMessage(
        int index,
        int totalChapters,
        long renderAttemptId,
        double? progress = 0,
        long? navigationGeneration = null,
        ReaderChapterRestoreTarget? restoreTarget = null)
    {
        var payload = new SetChapterPayload(
            index,
            totalChapters,
            renderAttemptId,
            progress,
            navigationGeneration,
            restoreTarget switch
            {
                ReaderChapterRestoreTarget.Start => "start",
                ReaderChapterRestoreTarget.End => "end",
                _ => null,
            });
        var message = new NovelReaderWebMessage<SetChapterPayload>(1, "setChapter", payload);
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string CreateSetChapterMessage(
        ReaderNavigationRenderRequest renderRequest,
        int totalChapters,
        long renderAttemptId)
    {
        ArgumentNullException.ThrowIfNull(renderRequest);
        return CreateSetChapterMessage(
            renderRequest.Destination.ChapterIndex,
            totalChapters,
            renderAttemptId,
            renderRequest.Destination.ExactProgress,
            renderRequest.Generation,
            renderRequest.Destination.RestoreTarget);
    }

    public static string CreateRestoreProgressMessage(
        double progress,
        long renderAttemptId,
        long? navigationGeneration = null)
    {
        var payload = new RestoreProgressPayload(
            progress,
            renderAttemptId,
            navigationGeneration);
        var message = new NovelReaderWebMessage<RestoreProgressPayload>(
            1,
            "restoreProgress",
            payload
        );
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string CreateJumpToFragmentMessage(
        string fragment,
        long renderAttemptId,
        long navigationGeneration)
    {
        var payload = new JumpToFragmentPayload(
            fragment,
            renderAttemptId,
            navigationGeneration);
        var message = new NovelReaderWebMessage<JumpToFragmentPayload>(
            1,
            "jumpToFragment",
            payload);
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string CreateNavigatePageMessage(string direction)
    {
        if (direction is not ("forward" or "backward"))
            throw new ArgumentOutOfRangeException(nameof(direction));

        var message = new NovelReaderWebMessage<NavigatePagePayload>(
            1,
            "navigatePage",
            new NavigatePagePayload(direction));
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string CreateSetWheelNavigationMessage(bool enabled)
    {
        var message = new NovelReaderWebMessage<SetWheelNavigationPayload>(
            1,
            "setWheelNavigation",
            new SetWheelNavigationPayload(enabled));
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string CreateSasayakiHighlightMessage(
        long generation,
        int startCodePoint,
        int length,
        bool autoScroll,
        string textColor,
        string backgroundColor)
    {
        if (generation < 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        if (startCodePoint < 0)
            throw new ArgumentOutOfRangeException(nameof(startCodePoint));
        if (length < 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        ArgumentNullException.ThrowIfNull(textColor);
        ArgumentNullException.ThrowIfNull(backgroundColor);

        var message = new NovelReaderWebMessage<SasayakiHighlightPayload>(
            1,
            "highlightSasayakiCue",
            new SasayakiHighlightPayload(
                generation,
                startCodePoint,
                length,
                autoScroll,
                textColor,
                backgroundColor));
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string CreateClearSasayakiHighlightMessage()
    {
        var message = new NovelReaderWebMessage<EmptyPayload>(
            1,
            "clearSasayakiHighlight",
            new EmptyPayload());
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    private sealed record SetChapterPayload(
        int Index,
        int TotalChapters,
        long RenderAttemptId,
        double? Progress,
        long? NavigationGeneration,
        string? RestoreTarget);

    private sealed record RestoreProgressPayload(
        double Progress,
        long RenderAttemptId,
        long? NavigationGeneration);

    private sealed record JumpToFragmentPayload(
        string Fragment,
        long RenderAttemptId,
        long NavigationGeneration);

    private sealed record NavigatePagePayload(string Direction);

    private sealed record SetWheelNavigationPayload(bool Enabled);

    private sealed record SasayakiHighlightPayload(
        long Generation,
        int StartCodePoint,
        int Length,
        bool AutoScroll,
        string TextColor,
        string BackgroundColor);

    private sealed record EmptyPayload;
}
