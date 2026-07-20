using System;
using System.Text;
using Niratan.Enums;
using Niratan.Models.Settings;

namespace Niratan.Services.Novels;

public static class NovelReaderContentStyles
{
    public static string GenerateCss(ReaderSettings settings, ThemeMode themeMode)
    {
        var bg = ARGBToCssColor(settings.BackgroundColor(themeMode));
        var fg = settings.TextColorCss(themeMode);
        var writingMode = settings.WritingModeCss;
        var pagePadding = settings.PagePaddingCss;
        var bottomPadding = settings.BottomPaddingCss;
        var columnGap = settings.ColumnGapCss;
        var imageMaxWidth = settings.ImageMaxWidthFallbackCss;
        var imageMaxHeight = settings.ImageMaxHeightFallbackCss;
        var horizontalSpread = settings.UsesTwoColumnHorizontalPages;
        var readerContentWidth = horizontalSpread
            ? "max(1px, calc((var(--page-width) - " + settings.HorizontalPadding + "vw - 32px) / 2))"
            : "max(1px, var(--page-width))";
        var readerColumnGap = horizontalSpread ? "32px" : columnGap;

        var sb = new StringBuilder();

        // Root variables and base html/body
        sb.Append(Css($$"""
            :root {
                --page-width: 100vw;
                --page-height: 100vh;
                --reader-safe-inline: 0px;
                --reader-safe-block: 0px;
                --reader-content-width: {{readerContentWidth}};
                --reader-column-gap: {{readerColumnGap}};
                --reader-content-height: max(1px, var(--page-height));
            }
            @media (prefers-color-scheme: light) {
                :root { --niratan-system-text-color: #000; }
            }
            @media (prefers-color-scheme: dark) {
                :root { --niratan-system-text-color: #fff; }
            }
            html, body {
                overflow: hidden !important;
                height: var(--page-height) !important;
                width: var(--page-width) !important;
                margin: 0 !important;
                padding: 0 !important;
                background: {{bg}} !important;
                color: {{fg}} !important;
            }
            html, body, .ttu-book-html-wrapper {
                writing-mode: {{writingMode}} !important;
                -webkit-writing-mode: {{writingMode}} !important;
            }
            html {
                -webkit-line-box-contain: block glyphs replaced;
            }
            """));
        sb.Append('\n');

        if (settings.ImportedFontUrl is { } importedFontUrl)
        {
            sb.Append(Css($$"""
            @font-face {
                font-family: {{ImportedFontFamily(settings.SelectedFont)}};
                src: url({{CssString(importedFontUrl)}});
                font-display: swap;
            }
            """));
            sb.Append('\n');
        }

        // Body: font, size, writing mode, column layout, padding
        sb.Append(Css($$"""
            body {
                font-family: {{settings.SelectedFont}} !important;
                font-size: {{settings.FontSize}}px !important;
                -webkit-text-size-adjust: none !important;
                box-sizing: border-box !important;
                text-orientation: mixed;
                column-width: var(--reader-content-width) !important;
                column-gap: var(--reader-column-gap) !important;
                padding: {{pagePadding}} !important;
                padding-bottom: {{bottomPadding}} !important;
                {{(horizontalSpread ? "column-count: 2 !important; -webkit-column-count: 2 !important;" : "")}}
            }
            """));
        sb.Append('\n');

        // Text alignment: if not justified, use start + hanging punctuation + strict line-break
        if (!settings.JustifyText)
            sb.Append($$"""
            body {
                text-align: start !important;
                hanging-punctuation: allow-end !important;
                line-break: strict !important;
            }
            """);
        sb.Append('\n');

        // Block elements
        sb.Append(Css($$"""
            p, div, section, article, blockquote, li {
                max-width: var(--reader-content-width) !important;
                white-space: normal !important;
                overflow-wrap: break-word !important;
            }
            """));
        sb.Append('\n');

        // Page break avoidance
        if (settings.AvoidPageBreak)
            sb.Append(Css($$"""
            p {
                break-inside: avoid !important;
                -webkit-column-break-inside: avoid !important;
            }
            """));

        // Advanced layout: line-height and letter-spacing
        if (settings.LayoutAdvanced)
        {
            var letterSpacingEm = (settings.CharacterSpacing / 100.0).ToString("F2",
                System.Globalization.CultureInfo.InvariantCulture);
            sb.Append(Css($$"""
            body {
                line-height: {{settings.LineHeight.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}} !important;
                letter-spacing: {{letterSpacingEm}}em !important;
            }
            """));
            sb.Append('\n');

            var paragraphSpacing = Math.Clamp(settings.ParagraphSpacing, 0, 3)
                .ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            var paragraphMargins = settings.VerticalWriting
                ? $"margin-right: {paragraphSpacing}em !important; margin-left: {paragraphSpacing}em !important;"
                : $"margin-top: {paragraphSpacing}em !important; margin-bottom: {paragraphSpacing}em !important;";
            sb.Append(Css($$"""
            p {
                {{paragraphMargins}}
            }
            """));
            sb.Append('\n');
        }

        // Furigana
        if (settings.HideFurigana)
            sb.Append("rt { display: none !important; }\n");
        else
            sb.Append("rt { font-size: 0.45em; }\n");

        sb.Append(Css($$"""
            ruby > rt, ruby > rp {
                -webkit-user-select: none;
                user-select: none;
            }
            ::highlight(niratan-selection) {
                background-color: rgba(160, 160, 160, 0.4) !important;
                color: inherit;
            }
            """));
        sb.Append('\n');

        // Image constraints
        sb.Append(Css($$"""
            img, svg, video, canvas, table, pre, code {
                max-width: 100% !important;
            }
            .niratan-blur-wrapper {
                display: table;
                margin: auto;
                line-height: 0;
                overflow: hidden;
                cursor: pointer;
            }
            img.block-img.niratan-blurred,
            svg.niratan-blurred {
                filter: blur(24px) !important;
                clip-path: inset(0);
                cursor: pointer;
            }
            img.block-img {
                max-width: var(--niratan-image-max-width, {{imageMaxWidth}}) !important;
                max-height: var(--niratan-image-max-height, {{imageMaxHeight}}) !important;
                width: auto !important;
                height: auto !important;
                display: block !important;
                margin: auto !important;
                break-inside: avoid !important;
                object-fit: contain !important;
            }
            svg {
                max-width: var(--niratan-image-max-width, {{imageMaxWidth}}) !important;
                max-height: var(--niratan-image-max-height, {{imageMaxHeight}}) !important;
                width: 100% !important;
                height: 100% !important;
                display: block !important;
                margin: auto !important;
                break-inside: avoid !important;
            }
            a { color: rgba(66, 108, 245, 1) !important; }
            """));

        return sb.ToString();
    }

    private static string ARGBToCssColor(uint argb)
    {
        if (argb == 0xFF000000) return "#000";
        if (argb == 0xFFFFFFFF) return "#FFF";
        if (argb == 0xFFF2E2C9) return "#F2E2C9";
        if (argb == 0xFF18150C) return "#18150C";
        return $"#{argb & 0x00FFFFFF:X6}";
    }

    private static string ImportedFontFamily(string selectedFont)
    {
        var comma = selectedFont.IndexOf(',');
        return (comma >= 0 ? selectedFont[..comma] : selectedFont).Trim();
    }

    private static string CssString(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static string GenerateCss(
        int fontSize = 22,
        string? fontFamily = null,
        string? textColor = null,
        string? backgroundColor = null,
        string writingMode = "horizontal-tb",
        int columnGap = 0,
        string horizontalPadding = "clamp(48px, 5vw, 112px)",
        string verticalPadding = "clamp(28px, 4vh, 72px)",
        int bottomPadding = 0,
        bool justifyText = false)
    {
        var fg = textColor ?? "#111111";
        var bg = backgroundColor ?? "#ffffff";
        var font = fontFamily ?? "system-ui, sans-serif";
        var textAlign = justifyText ? "justify" : "start";

        var sb = new StringBuilder();
        sb.Append(Css($$"""
            :root {
                --page-width: 100vw;
                --page-height: 100vh;
                --reader-safe-inline: {{horizontalPadding}};
                --reader-safe-block: {{verticalPadding}};
                --reader-content-width: max(1px, calc(var(--page-width) - (var(--reader-safe-inline) * 2)));
                --reader-column-gap: calc(var(--reader-safe-inline) * 2);
                --reader-content-height: max(1px, calc(var(--page-height) - (var(--reader-safe-block) * 2) - {{bottomPadding}}px));
            }
            html, body {
                overflow: hidden !important;
                height: var(--page-height) !important;
                width: var(--page-width) !important;
                margin: 0 !important;
                padding: 0 !important;
                background: {{bg}} !important;
                color: {{fg}} !important;
            }
            html, body, .ttu-book-html-wrapper {
                writing-mode: {{writingMode}} !important;
                -webkit-writing-mode: {{writingMode}} !important;
            }
            html {
                -webkit-line-box-contain: block glyphs replaced;
            }
            body {
                font-family: {{font}} !important;
                font-size: {{fontSize}}px !important;
                -webkit-text-size-adjust: none !important;
                box-sizing: border-box !important;
                column-width: var(--reader-content-width) !important;
                column-gap: var(--reader-column-gap) !important;
                padding: var(--reader-safe-block) var(--reader-safe-inline) !important;
                padding-bottom: {{bottomPadding}}px !important;
                text-align: {{textAlign}} !important;
                hanging-punctuation: allow-end !important;
                line-break: strict !important;
                word-break: normal !important;
                white-space: normal !important;
                text-orientation: mixed;
                overflow-wrap: break-word !important;
            }
            p, div, section, article, blockquote, li {
                max-width: var(--reader-content-width) !important;
                white-space: normal !important;
                overflow-wrap: break-word !important;
            }
            img, svg, video, canvas, table, pre, code {
                max-width: 100% !important;
            }
            img.block-img {
                max-width: var(--niratan-image-max-width, var(--reader-content-width)) !important;
                max-height: var(--niratan-image-max-height, var(--reader-content-height)) !important;
                width: auto !important;
                height: auto !important;
                display: block !important;
                margin: auto !important;
                break-inside: avoid !important;
                object-fit: contain !important;
            }
            svg {
                max-width: var(--niratan-image-max-width, var(--reader-content-width)) !important;
                max-height: var(--niratan-image-max-height, var(--reader-content-height)) !important;
                width: 100% !important;
                height: 100% !important;
                display: block !important;
                margin: auto !important;
                break-inside: avoid !important;
            }
            ruby > rt, ruby > rp {
                -webkit-user-select: none;
                user-select: none;
            }
            ::highlight(niratan-selection) {
                background-color: rgba(160, 160, 160, 0.4) !important;
                color: inherit;
            }
            p {
                break-inside: avoid !important;
            }
            """));
        return sb.ToString();
    }

    public static string GenerateScriptTag(string css) =>
        $"var s=document.createElement('style');s.textContent={JavaScriptStringLiteral(css)};document.head.appendChild(s);";

    public static string JavaScriptStringLiteral(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Css(string value) => value;
}
