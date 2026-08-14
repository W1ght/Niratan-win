using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

/// <summary>
/// Projects Jellyfin-compatible folder ownership and release-bundle evidence into catalog
/// identities without touching source media. Top-level show folders are hard grouping boundaries;
/// only a flat source/show root falls back to a dominant parsed title.
/// </summary>
internal static class VideoScanBundleClassifier
{
    private static readonly Regex SeasonFolderPattern = new(
        @"^(?:(?:Season|Series)[ ._-]*(?<word>\d{1,3})|S(?<short>\d{1,3})|第\s*(?<cjk>\d{1,3})\s*季)(?:$|[ ._-].*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ExplicitSeasonZeroPattern = new(
        @"(?<![A-Za-z0-9])S0{1,3}[ ._-]*E\d{1,4}(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyDictionary<string, ParsedVideoIdentity> Parse(
        IReadOnlyList<string> paths,
        string sourceRoot,
        VideoLibraryMediaType mediaType,
        IVideoFileNameParser parser)
    {
        var root = Path.GetFullPath(sourceRoot);
        var parsed = new Dictionary<string, ParsedVideoIdentity>(StringComparer.OrdinalIgnoreCase);
        var contexts = new List<PathContext>(paths.Count);
        foreach (var path in paths)
        {
            var fullPath = Path.GetFullPath(path);
            var segments = RelativeDirectorySegments(fullPath, root);
            var folderSeason = FindFolderSeason(segments);
            var item = parser.Parse(fullPath, root, mediaType);
            if (mediaType == VideoLibraryMediaType.Movie)
            {
                // An explicitly configured movie library owns standalone movies. Tokens such as
                // OVA, PV, or S01E01 can be part of a movie title and must not manufacture a
                // Series -> Specials -> Episode hierarchy during the first scan.
                parsed[fullPath] = item with
                {
                    SeasonNumber = null,
                    EpisodeStart = null,
                    EpisodeEnd = null,
                    AbsoluteEpisodeNumber = null,
                    SpecialKind = ParsedVideoSpecialKind.None,
                    IsMultiEpisode = false,
                    HasEpisodeEvidence = false,
                    EpisodeTitle = null,
                };
                continue;
            }
            if (!item.SeasonNumber.HasValue && folderSeason.HasValue)
                item = item with { SeasonNumber = folderSeason };
            if (item.SeasonNumber == 0 && item.SpecialKind == ParsedVideoSpecialKind.None)
                item = item with { SpecialKind = ParsedVideoSpecialKind.Special };

            var preservesSpecialNumber = PreservesSpecialNumber(fullPath, segments);
            if (item.SpecialKind != ParsedVideoSpecialKind.None && !preservesSpecialNumber)
            {
                item = item with
                {
                    SeasonNumber = 0,
                    EpisodeStart = null,
                    EpisodeEnd = null,
                    AbsoluteEpisodeNumber = null,
                    IsMultiEpisode = false,
                    HasEpisodeEvidence = true,
                    EpisodeTitle = BuildSpecialTitle(item),
                };
            }

            parsed[fullPath] = item;
            contexts.Add(new PathContext(
                fullPath,
                segments,
                ResolveAnchorKey(segments),
                preservesSpecialNumber));
        }

        if (mediaType == VideoLibraryMediaType.Movie)
            return parsed;

        foreach (var group in contexts.GroupBy(context => context.AnchorKey, StringComparer.OrdinalIgnoreCase))
            ClassifyGroup(group.ToList(), root, parsed);

        return parsed;
    }

    private static void ClassifyGroup(
        IReadOnlyList<PathContext> group,
        string root,
        IDictionary<string, ParsedVideoIdentity> parsed)
    {
        var folderFirst = group[0].AnchorKey != "."
                          || group.Any(context => context.Segments.Any(IsStructuralFolder));
        var main = group
            .Where(context => parsed[context.Path] is { HasEpisodeEvidence: true, SpecialKind: ParsedVideoSpecialKind.None })
            .ToList();
        if (main.Count == 0)
        {
            NormalizeSpecials(
                group,
                parsed,
                folderFirst ? ResolveAnchorTitle(group[0].AnchorKey, root) : null);
            return;
        }

        var titleGroups = main
            .Select(context => parsed[context.Path])
            .Where(item => !string.IsNullOrWhiteSpace(item.NormalizedTitle))
            .GroupBy(item => NormalizeKey(item.NormalizedTitle))
            .Where(candidate => candidate.Key.Length > 0)
            .OrderByDescending(candidate => candidate.Count())
            .ThenByDescending(candidate => candidate.Key.Length)
            .ToList();
        var dominant = titleGroups.FirstOrDefault();
        var hasDominant = dominant != null && dominant.Count() * 5 >= main.Count * 3;
        if (!hasDominant && !folderFirst)
            return;

        var canonicalTitle = hasDominant
            ? dominant!.First().NormalizedTitle
            : ResolveAnchorTitle(group[0].AnchorKey, root);
        if (string.IsNullOrWhiteSpace(canonicalTitle))
            return;

        foreach (var context in main)
        {
            var item = parsed[context.Path];
            if (folderFirst || NormalizeKey(item.NormalizedTitle) == dominant!.Key)
                parsed[context.Path] = item with { NormalizedTitle = canonicalTitle };
        }

        NormalizeSpecials(
            group,
            parsed,
            canonicalTitle,
            folderFirst ? null : dominant!.Key);
    }

    private static void NormalizeSpecials(
        IReadOnlyList<PathContext> group,
        IDictionary<string, ParsedVideoIdentity> parsed,
        string? canonicalTitle,
        string? requiredTitleKey = null)
    {
        var specials = group
            .Where(context => parsed[context.Path].SpecialKind != ParsedVideoSpecialKind.None)
            .OrderBy(context => context.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var context in specials)
        {
            var item = parsed[context.Path];
            var ownerTitle = requiredTitleKey == null
                             || NormalizeKey(item.NormalizedTitle) == requiredTitleKey
                ? canonicalTitle
                : null;
            if (context.PreservesSpecialNumber && item.EpisodeStart.HasValue)
            {
                parsed[context.Path] = item with
                {
                    NormalizedTitle = ownerTitle ?? item.NormalizedTitle,
                    SeasonNumber = 0,
                    AbsoluteEpisodeNumber = null,
                    HasEpisodeEvidence = true,
                    EpisodeTitle = BuildSpecialTitle(item),
                };
                continue;
            }

            parsed[context.Path] = item with
            {
                NormalizedTitle = ownerTitle ?? item.NormalizedTitle,
                SeasonNumber = 0,
                EpisodeStart = null,
                EpisodeEnd = null,
                AbsoluteEpisodeNumber = null,
                IsMultiEpisode = false,
                HasEpisodeEvidence = true,
                EpisodeTitle = BuildSpecialTitle(item),
            };
        }
    }

    private static string BuildSpecialTitle(ParsedVideoIdentity item)
    {
        if (!string.IsNullOrWhiteSpace(item.EpisodeTitle))
            return item.EpisodeTitle;
        var number = item.EpisodeStart.HasValue ? $" {item.EpisodeStart:00}" : string.Empty;
        return item.SpecialKind switch
        {
            ParsedVideoSpecialKind.NcOp => "NCOP" + number,
            ParsedVideoSpecialKind.NcEd => "NCED" + number,
            ParsedVideoSpecialKind.Preview => "PV" + number,
            ParsedVideoSpecialKind.Menu => "Disc Menu" + number,
            ParsedVideoSpecialKind.Ova => "OVA" + number,
            ParsedVideoSpecialKind.Oad => "OAD" + number,
            ParsedVideoSpecialKind.Short when !string.IsNullOrWhiteSpace(item.NormalizedTitle)
                => item.NormalizedTitle + number,
            _ when !string.IsNullOrWhiteSpace(item.NormalizedTitle) => item.NormalizedTitle + number,
            _ => "Special" + number,
        };
    }

    private static string ResolveAnchorKey(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0 || IsStructuralFolder(segments[0]))
            return ".";
        return segments[0];
    }

    private static string ResolveAnchorTitle(string anchorKey, string root) =>
        (anchorKey == "." ? new DirectoryInfo(root).Name : anchorKey)
        .Normalize(NormalizationForm.FormKC)
        .Trim();

    private static IReadOnlyList<string> RelativeDirectorySegments(string path, string root)
    {
        var directory = Path.GetDirectoryName(path) ?? root;
        var relative = Path.GetRelativePath(root, directory);
        return relative == "."
            ? []
            : relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static int? FindFolderSeason(IReadOnlyList<string> segments)
    {
        for (var index = segments.Count - 1; index >= 0; index--)
        {
            if (TryParseSeasonFolder(segments[index], out var season))
                return season;
        }
        return segments.Any(IsExtraFolder) ? 0 : null;
    }

    private static bool PreservesSpecialNumber(string path, IReadOnlyList<string> segments) =>
        ExplicitSeasonZeroPattern.IsMatch(Path.GetFileNameWithoutExtension(path))
        || segments.Any(segment => TryParseSeasonFolder(segment, out var season) && season == 0);

    private static bool IsStructuralFolder(string value) =>
        TryParseSeasonFolder(value, out _) || IsExtraFolder(value);

    private static bool TryParseSeasonFolder(string value, out int season)
    {
        var key = NormalizeFolderKey(value);
        if (key is "special" or "specials")
        {
            season = 0;
            return true;
        }

        var match = SeasonFolderPattern.Match(value.Normalize(NormalizationForm.FormKC).Trim());
        foreach (var groupName in new[] { "word", "short", "cjk" })
        {
            if (match.Success && int.TryParse(match.Groups[groupName].Value, out season))
                return true;
        }
        season = 0;
        return false;
    }

    private static bool IsExtraFolder(string value) => NormalizeFolderKey(value) is
        "ncop" or "nced" or "ncop nced"
        or "pv" or "preview" or "previews" or "promo" or "promos" or "trailer" or "trailers"
        or "menu" or "menus" or "disc menu" or "disc menus"
        or "short" or "shorts" or "mini anime" or "mini animation"
        or "迷你动画" or "迷你動畫" or "短篇" or "小剧场" or "小劇場"
        or "extra" or "extras" or "featurette" or "featurettes"
        or "behind the scenes" or "deleted scenes" or "interviews" or "scenes" or "clips"
        or "samples" or "花絮" or "特典" or "映像特典";

    private static string NormalizeFolderKey(string value) => Regex.Replace(
            value.Normalize(NormalizationForm.FormKC).Replace('&', ' '),
            @"[\s._-]+",
            " ",
            RegexOptions.CultureInvariant)
        .Trim()
        .ToLowerInvariant();

    private static string NormalizeKey(string value) => string.Concat(
        value.Where(char.IsLetterOrDigit)).ToUpperInvariant();

    private sealed record PathContext(
        string Path,
        IReadOnlyList<string> Segments,
        string AnchorKey,
        bool PreservesSpecialNumber);
}
