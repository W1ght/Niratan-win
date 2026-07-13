using System;
using System.Collections.Generic;
using System.Linq;
using HtmlAgilityPack;

namespace Hoshi.Services.Novels;

public static class EpubActiveContentSanitizer
{
    private static readonly HashSet<string> RemovedElements = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "script",
        "iframe",
        "frame",
        "frameset",
        "object",
        "embed",
        "applet",
        "base",
        "foreignObject",
        "animate",
        "animateMotion",
        "animateTransform",
        "set",
        "discard",
    };

    private static readonly HashSet<string> UrlAttributes = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "href",
        "src",
        "xlink:href",
        "action",
        "formaction",
        "data",
        "poster",
        "background",
        "cite",
        "longdesc",
        "profile",
        "manifest",
        "srcset",
    };

    public static string Sanitize(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var document = new HtmlDocument
        {
            OptionAutoCloseOnEnd = true,
            OptionCheckSyntax = false,
            OptionOutputAsXml = true,
        };
        document.LoadHtml(html);

        var nodes = document.DocumentNode.DescendantsAndSelf().ToArray();
        foreach (var node in nodes)
        {
            if (node.ParentNode == null && node != document.DocumentNode)
                continue;

            if (RemovedElements.Contains(node.Name)
                || IsRefreshMeta(node)
                || IsExecutableLink(node))
            {
                node.Remove();
                continue;
            }

            foreach (var attribute in node.Attributes.ToArray())
            {
                var qualifiedName = attribute.Name;
                var localName = qualifiedName.Contains(':', StringComparison.Ordinal)
                    ? qualifiedName[(qualifiedName.LastIndexOf(':') + 1)..]
                    : qualifiedName;
                if (localName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || localName.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)
                    || UrlAttributes.Contains(qualifiedName)
                        && IsDangerousUrl(attribute.Value))
                {
                    node.Attributes.Remove(attribute);
                }
            }
        }

        return document.DocumentNode.OuterHtml;
    }

    public static bool IsDangerousUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var decoded = HtmlEntity.DeEntitize(value).TrimStart();
        var compact = new string(decoded
            .Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character))
            .ToArray());
        if (compact.Contains(",javascript:", StringComparison.OrdinalIgnoreCase)
            || compact.Contains(",vbscript:", StringComparison.OrdinalIgnoreCase)
            || compact.Contains(",data:text/html", StringComparison.OrdinalIgnoreCase)
            || compact.Contains(",data:application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || compact.Contains(",data:application/javascript", StringComparison.OrdinalIgnoreCase)
            || compact.Contains(",data:text/javascript", StringComparison.OrdinalIgnoreCase)
            || compact.Contains(",data:image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var colon = decoded.IndexOf(':');
        if (colon <= 0)
            return false;

        var scheme = new string(decoded[..colon]
            .Where(character => !char.IsWhiteSpace(character) && !char.IsControl(character))
            .ToArray());
        if (scheme.Equals("javascript", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("vbscript", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
            return false;

        var mediaType = decoded[(colon + 1)..]
            .TrimStart()
            .Split([',', ';'], 2, StringSplitOptions.TrimEntries)[0];
        return mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/javascript", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRefreshMeta(HtmlNode node) =>
        node.Name.Equals("meta", StringComparison.OrdinalIgnoreCase)
        && node.GetAttributeValue("http-equiv", "")
            .Equals("refresh", StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutableLink(HtmlNode node)
    {
        if (!node.Name.Equals("link", StringComparison.OrdinalIgnoreCase))
            return false;

        var relations = node.GetAttributeValue("rel", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return relations.Any(relation =>
            relation.Equals("import", StringComparison.OrdinalIgnoreCase)
            || relation.Equals("modulepreload", StringComparison.OrdinalIgnoreCase));
    }
}
