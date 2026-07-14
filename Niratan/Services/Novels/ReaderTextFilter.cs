using System.Text;
using System.Text.RegularExpressions;

namespace Niratan.Services.Novels;

internal static partial class ReaderTextFilter
{
    public static int CountReadableCharacters(string html)
    {
        var count = 0;
        foreach (var rune in VisibleText(html).EnumerateRunes())
        {
            if (IsReaderMatchableCodePoint(rune.Value))
                count++;
        }

        return count;
    }

    public static string FilteredReaderText(string html)
    {
        var builder = new StringBuilder(html.Length);
        foreach (var rune in VisibleText(html).EnumerateRunes())
        {
            if (IsReaderMatchableCodePoint(rune.Value))
                builder.Append(rune);
        }

        return builder.ToString();
    }

    public static string VisibleText(string html)
    {
        var text = html;
        var bodyMatch = BodyRegex().Match(text);
        if (bodyMatch.Success)
            text = bodyMatch.Value;

        text = RubyAnnotationRegex().Replace(text, "");
        text = ScriptOrStyleRegex().Replace(text, "");
        text = TagRegex().Replace(text, "");
        return text
            .Replace("&nbsp;", " ")
            .Replace("&amp;", "&")
            .Replace("&lt;", "<")
            .Replace("&gt;", ">");
    }

    public static bool IsReaderMatchableCodePoint(int codePoint) =>
        codePoint is >= '0' and <= '9'
            or >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or 0x25CB
            or 0x25EF
            or >= 0x3005 and <= 0x3007
            or 0x303B
            or >= 0x3041 and <= 0x3096
            or >= 0x309D and <= 0x309E
            or >= 0x30A1 and <= 0x30FA
            or 0x30FC
            or >= 0xFF10 and <= 0xFF19
            or >= 0xFF21 and <= 0xFF3A
            or >= 0xFF41 and <= 0xFF5A
            or >= 0xFF66 and <= 0xFF9D
            or >= 0x2E80 and <= 0x2FDF
            or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0x20000 and <= 0x2A6DF
            or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F
            or >= 0x2B820 and <= 0x2CEAF
            or >= 0x2CEB0 and <= 0x2EBEF
            or >= 0x30000 and <= 0x3134F
            or >= 0x31350 and <= 0x323AF;

    [GeneratedRegex("(?is)<body[^>]*>.*?</body>")]
    private static partial Regex BodyRegex();

    [GeneratedRegex("(?is)<rt[^>]*>.*?</rt>")]
    private static partial Regex RubyAnnotationRegex();

    [GeneratedRegex("(?is)<(script|style)[^>]*>.*?</\\1>")]
    private static partial Regex ScriptOrStyleRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
}
