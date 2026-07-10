using System.Globalization;
using System.Text.RegularExpressions;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Dictionary;

internal static partial class DictionaryPopupScaleCss
{
    public static string BuildDeclarations(double requestedScale)
    {
        var scale = DictionaryPopupAppearanceConstraints.NormalizeScale(requestedScale);
        string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        string Px(double value) => $"{Number(value * scale)}px";

        return string.Join(string.Empty,
            $"--popup-scale:{Number(scale)};",
            $"--popup-root-font-size:{Px(16)};",
            $"--popup-body-font-size:{Px(15)};",
            $"--popup-dictionary-font-size:{Px(14)};",
            $"--popup-expression-font-size:{Px(26)};",
            $"--popup-expression-reading-size:{Px(13)};",
            $"--popup-tag-font-size:{Px(11)};",
            $"--popup-small-tag-font-size:{Px(10)};",
            $"--popup-dict-label-font-size:{Px(10)};",
            $"--popup-pitch-font-size:{Px(13)};",
            $"--popup-arrow-size:{Px(8)};",
            $"--popup-overlay-close-size:{Px(20)};",
            $"--popup-button-size:{Px(28)};",
            $"--popup-space-1:{Px(1)};",
            $"--popup-space-2:{Px(2)};",
            $"--popup-space-3:{Px(3)};",
            $"--popup-space-4:{Px(4)};",
            $"--popup-space-5:{Px(5)};",
            $"--popup-space-6:{Px(6)};",
            $"--popup-space-8:{Px(8)};",
            $"--popup-space-10:{Px(10)};",
            $"--popup-space-12:{Px(12)};",
            $"--popup-space-18:{Px(18)};",
            $"--popup-space-20:{Px(20)};",
            $"--popup-space-neg-2:{Px(-2)};",
            $"--popup-space-neg-4:{Px(-4)};");
    }

    public static string ScaleCustomCss(string css) =>
        PixelLengthRegex().Replace(
            css ?? string.Empty,
            match => $"calc({match.Groups[1].Value}px * var(--popup-scale))");

    [GeneratedRegex(@"(-?(?:\d+(?:\.\d+)?|\.\d+))px", RegexOptions.CultureInvariant)]
    private static partial Regex PixelLengthRegex();
}
