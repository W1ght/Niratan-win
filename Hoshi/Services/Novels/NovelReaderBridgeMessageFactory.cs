using System.Text.Json;
using System.Text.Json.Serialization;
using Hoshi.Models.DTO;

namespace Hoshi.Services.Novels;

public static class NovelReaderBridgeMessageFactory
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

    public static string CreateSetChapterMessage(int index, int totalChapters, double progress = 0)
    {
        var payload = new SetChapterPayload(index, totalChapters, progress);
        var message = new NovelReaderWebMessage<SetChapterPayload>(1, "setChapter", payload);
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    public static string CreateRestoreProgressMessage(double progress)
    {
        var payload = new RestoreProgressPayload(progress);
        var message = new NovelReaderWebMessage<RestoreProgressPayload>(
            1,
            "restoreProgress",
            payload
        );
        return JsonSerializer.Serialize(message, JsonOptions);
    }

    private sealed record SetChapterPayload(int Index, int TotalChapters, double Progress);

    private sealed record RestoreProgressPayload(double Progress);
}
