using System;

namespace Hoshi.Models.Video;

public sealed class VideoSmartRule
{
    public VideoSmartRule()
    {
    }

    public VideoSmartRule(VideoSmartRuleField field, string value)
    {
        Field = field;
        Value = value;
    }

    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public VideoSmartRuleField Field { get; set; }
    public VideoSmartRuleMatch Match { get; set; } = VideoSmartRuleMatch.Contains;
    public string Value { get; set; } = string.Empty;
}
