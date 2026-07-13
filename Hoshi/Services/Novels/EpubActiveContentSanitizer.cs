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

    private static readonly HashSet<string> UrlAttributeLocalNames = new(
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

            if (RemovedElements.Contains(LocalName(node.Name))
                || IsRefreshMeta(node)
                || IsExecutableLink(node))
            {
                node.Remove();
                continue;
            }

            foreach (var attribute in node.Attributes.ToArray())
            {
                var qualifiedName = attribute.Name;
                var localName = LocalName(qualifiedName);
                if (localName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || localName.Equals("srcdoc", StringComparison.OrdinalIgnoreCase)
                    || localName.Equals("base", StringComparison.OrdinalIgnoreCase)
                    || IsUrlAttribute(node, attribute, localName)
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
        LocalName(node.Name).Equals("meta", StringComparison.OrdinalIgnoreCase)
        && node.GetAttributeValue("http-equiv", "")
            .Equals("refresh", StringComparison.OrdinalIgnoreCase);

    private static bool IsExecutableLink(HtmlNode node)
    {
        if (!LocalName(node.Name).Equals("link", StringComparison.OrdinalIgnoreCase))
            return false;

        var relations = node.GetAttributeValue("rel", "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return relations.Any(relation =>
            relation.Equals("import", StringComparison.OrdinalIgnoreCase)
            || relation.Equals("modulepreload", StringComparison.OrdinalIgnoreCase));
    }

    private static string LocalName(string qualifiedName)
    {
        var separator = qualifiedName.LastIndexOf(':');
        return separator >= 0 ? qualifiedName[(separator + 1)..] : qualifiedName;
    }

    private static bool IsUrlAttribute(
        HtmlNode node,
        HtmlAttribute attribute,
        string localName)
    {
        if (!UrlAttributeLocalNames.Contains(localName))
            return false;

        var namespaceUri = ResolveAttributeNamespaceUri(node, attribute.Name);
        return string.IsNullOrEmpty(namespaceUri)
            || namespaceUri.Equals("http://www.w3.org/1999/xhtml", StringComparison.Ordinal)
            || namespaceUri.Equals("http://www.w3.org/1999/xlink", StringComparison.Ordinal)
            || namespaceUri.Equals("http://www.w3.org/2000/svg", StringComparison.Ordinal)
            || namespaceUri.Equals("http://www.w3.org/1998/Math/MathML", StringComparison.Ordinal)
            || IsDangerousUrl(attribute.Value);
    }

    private static string? ResolveAttributeNamespaceUri(
        HtmlNode node,
        string qualifiedName)
    {
        var separator = qualifiedName.IndexOf(':');
        if (separator < 0)
            return string.Empty;

        var prefix = qualifiedName[..separator];
        if (prefix.Equals("xml", StringComparison.OrdinalIgnoreCase))
            return "http://www.w3.org/XML/1998/namespace";

        var declarationName = $"xmlns:{prefix}";
        for (var current = node; current != null; current = current.ParentNode)
        {
            var declaration = current.Attributes.FirstOrDefault(candidate =>
                candidate.Name.Equals(declarationName, StringComparison.OrdinalIgnoreCase));
            if (declaration != null)
                return HtmlEntity.DeEntitize(declaration.Value).Trim();
        }

        return null;
    }
}
