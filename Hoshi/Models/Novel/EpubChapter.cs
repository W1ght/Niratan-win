namespace Hoshi.Models.Novel;

public sealed class EpubChapter
{
    public string Id { get; set; } = string.Empty;
    public string Href { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public bool IsLinear { get; set; } = true;
    public int SpineIndex { get; set; }
}
