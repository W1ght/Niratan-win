using System.Collections.Generic;

namespace Hoshi.Models.Novel;

public sealed class EpubTocItem
{
    public string Label { get; set; } = string.Empty;
    public string? Href { get; set; }
    public List<EpubTocItem> Children { get; set; } = [];
}
