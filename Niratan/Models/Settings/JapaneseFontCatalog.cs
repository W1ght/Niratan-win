using System;
using System.Collections.Generic;
using System.Linq;

namespace Niratan.Models.Settings;

public sealed record JapaneseFontOption(string Name, string ReaderCssValue, string SubtitleFontFamily);

public static class JapaneseFontCatalog
{
    public const string DefaultReaderCssValue = "'Klee One', 'Yu Mincho', serif";
    public const string DefaultSubtitleFontFamily = "Noto Serif CJK JP";

    public static IReadOnlyList<JapaneseFontOption> Fonts { get; } =
    [
        new("System Default", "system-ui, sans-serif", ""),
        new("Klee One", DefaultReaderCssValue, "Klee One"),
        new("Noto Serif CJK JP", "'Noto Serif CJK JP', serif", "Noto Serif CJK JP"),
        new("Noto Sans CJK JP", "'Noto Sans CJK JP', sans-serif", "Noto Sans CJK JP"),
        new("Yu Mincho", "'Yu Mincho', serif", "Yu Mincho"),
        new("Yu Gothic", "'Yu Gothic', sans-serif", "Yu Gothic"),
        new("MS Mincho", "'MS Mincho', serif", "MS Mincho"),
        new("MS Gothic", "'MS Gothic', sans-serif", "MS Gothic"),
        new("SimSun", "SimSun, serif", "SimSun"),
        new("Microsoft YaHei", "'Microsoft YaHei', sans-serif", "Microsoft YaHei"),
    ];

    public static JapaneseFontOption DefaultFont { get; } = Fonts[2];

    public static JapaneseFontOption? FindByReaderCssValue(string? cssValue) =>
        Fonts.FirstOrDefault(
            font => string.Equals(font.ReaderCssValue, cssValue, StringComparison.Ordinal));

    public static JapaneseFontOption? FindBySubtitleFontFamily(string? fontFamily) =>
        Fonts.FirstOrDefault(
            font => string.Equals(font.SubtitleFontFamily, Normalize(fontFamily), StringComparison.Ordinal));

    private static string Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim();
}
