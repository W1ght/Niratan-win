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
}
