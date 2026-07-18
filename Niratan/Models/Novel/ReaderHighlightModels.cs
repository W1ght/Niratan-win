using System;

namespace Niratan.Models.Novel;

public enum ReaderHighlightColor
{
    Yellow,
    Green,
    Blue,
    Pink,
    Purple,
}

public sealed record ReaderHighlight(
    Guid Id,
    int Character,
    int Offset,
    string Text,
    ReaderHighlightColor Color,
    DateTimeOffset CreatedAt);

public sealed record ReaderHighlightSelection(
    int Start,
    int Offset,
    string Text);

public sealed record ReaderHighlightJumpTarget(
    int ChapterIndex,
    double ChapterProgress);

public sealed record ReaderHighlightListItem(
    ReaderHighlight Highlight,
    ReaderHighlightJumpTarget JumpTarget,
    string ChapterLabel)
{
    public Guid Id => Highlight.Id;
    public string Text => Highlight.Text.Trim();
    public Windows.UI.Color SwatchColor => Highlight.Color switch
    {
        ReaderHighlightColor.Yellow => Windows.UI.Color.FromArgb(255, 239, 209, 56),
        ReaderHighlightColor.Green => Windows.UI.Color.FromArgb(255, 152, 220, 129),
        ReaderHighlightColor.Blue => Windows.UI.Color.FromArgb(255, 149, 185, 255),
        ReaderHighlightColor.Pink => Windows.UI.Color.FromArgb(255, 255, 155, 180),
        ReaderHighlightColor.Purple => Windows.UI.Color.FromArgb(255, 197, 175, 251),
        _ => Windows.UI.Color.FromArgb(255, 239, 209, 56),
    };
    public string PositionText => $"{ChapterLabel} - {Highlight.Character:N0}";
    public string CreatedText => Highlight.CreatedAt.ToLocalTime().ToString("g");
}
