using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public enum ParsedVideoSpecialKind
{
    None,
    Special,
    Ova,
    Oad,
    NcOp,
    NcEd,
}

public sealed record ParsedVideoIdentity(
    string OriginalName,
    string NormalizedTitle,
    string? FolderTitle,
    int? Year,
    int? SeasonNumber,
    int? EpisodeStart,
    int? EpisodeEnd,
    int? AbsoluteEpisodeNumber,
    int? SeriesPart,
    int? Cour,
    ParsedVideoSpecialKind SpecialKind,
    bool IsMultiEpisode,
    bool HasEpisodeEvidence,
    ImmutableDictionary<string, string> ExternalIds,
    ImmutableArray<string> RemovedReleaseTags);

public interface IVideoFileNameParser
{
    ParsedVideoIdentity Parse(
        string filePath,
        string? sourceRoot = null,
        VideoLibraryMediaType mediaType = VideoLibraryMediaType.Auto);
}

internal sealed class VideoFileNameParser : IVideoFileNameParser
{
    private static readonly Regex ExternalIdPattern = new(
        @"\[(?<provider>tmdbid|tvdbid|anidbid|anilistid|malid|bgmid|bangumiid)-(?<id>[A-Za-z0-9_-]+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SeasonEpisodePattern = new(
        @"(?<![A-Za-z0-9])S(?<season>\d{1,3})[ ._-]*E(?<start>\d{1,4})(?:[ ._-]*(?:-|~|～|–|—)[ ._-]*E?(?<end>\d{1,4}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex XEpisodePattern = new(
        @"(?<!\d)(?<season>\d{1,3})x(?<start>\d{1,4})(?:\s*(?:-|~|～|–|—)\s*(?<end>\d{1,4}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JapaneseEpisodePattern = new(
        @"(?:第\s*)?(?<start>\d{1,4})(?:\s*(?:-|~|～|–|—)\s*(?<end>\d{1,4}))?\s*話",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EnglishEpisodePattern = new(
        @"(?<![A-Za-z0-9])(?:EP?|Episode)[ ._-]*(?<start>\d{1,4})(?:[ ._-]*(?:-|~|～|–|—)[ ._-]*(?:EP?)?(?<end>\d{1,4}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SeriesPartPattern = new(
        @"第\s*(?<part>\d{1,3})\s*期",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex CourPattern = new(
        @"(?<![A-Za-z0-9])(?:cour|クール)[ ._-]*(?<cour>\d{1,2})|(?<courPrefix>\d{1,2})[ ._-]*(?:cour|クール)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex YearPattern = new(
        @"(?<![\dXx])(?<year>(?:19|20)\d{2})(?![\dXx])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BracketPattern = new(
        @"(?:\[(?<value>[^\]]{1,120})\]|【(?<value>[^】]{1,120})】|\((?<value>[^)]{1,120})\)|（(?<value>[^）]{1,120})）)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LeadingBracketPattern = new(
        @"^\s*\[(?<value>[^\]]{1,60})\]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AbsoluteEpisodePattern = new(
        @"(?:^|[ ._\-\[])#?(?<absolute>\d{2,4})(?:v\d+)?(?:$|[ ._\-\]])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SeparatorPattern = new(
        @"[\s._\-]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> ReleaseTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "720p", "1080p", "1080i", "2160p", "4k", "8k", "uhd", "hdr", "hdr10",
        "dv", "dolby vision", "bluray", "blu-ray", "bdrip", "bdremux", "web", "web-dl",
        "webrip", "hdtv", "dvd", "dvdrip", "x264", "x265", "h264", "h265", "hevc",
        "av1", "vp9", "aac", "flac", "opus", "ac3", "eac3", "dts", "truehd",
        "10bit", "8bit", "hi10p", "remux", "proper", "repack", "multi", "dual audio",
    };

    public ParsedVideoIdentity Parse(
        string filePath,
        string? sourceRoot = null,
        VideoLibraryMediaType mediaType = VideoLibraryMediaType.Auto)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var original = Path.GetFileNameWithoutExtension(filePath);
        var normalized = original.Normalize(NormalizationForm.FormKC);
        var externalIds = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
        normalized = ExternalIdPattern.Replace(normalized, match =>
        {
            var provider = NormalizeProviderId(match.Groups["provider"].Value);
            externalIds[provider] = match.Groups["id"].Value;
            return " ";
        });

        int? season = null;
        int? episodeStart = null;
        int? episodeEnd = null;
        var episodeMatch = FirstSuccessfulMatch(
            normalized,
            SeasonEpisodePattern,
            XEpisodePattern,
            JapaneseEpisodePattern,
            EnglishEpisodePattern);
        if (episodeMatch.Success)
        {
            season = ParseOptionalInt(episodeMatch, "season");
            episodeStart = ParseOptionalInt(episodeMatch, "start");
            episodeEnd = ParseOptionalInt(episodeMatch, "end") ?? episodeStart;
            normalized = RemoveMatch(normalized, episodeMatch);
        }

        var partMatch = SeriesPartPattern.Match(normalized);
        var seriesPart = ParseOptionalInt(partMatch, "part");
        if (partMatch.Success)
            normalized = RemoveMatch(normalized, partMatch);

        var courMatch = CourPattern.Match(normalized);
        var cour = ParseOptionalInt(courMatch, "cour") ?? ParseOptionalInt(courMatch, "courPrefix");
        if (courMatch.Success)
            normalized = RemoveMatch(normalized, courMatch);

        var yearMatch = YearPattern.Match(normalized);
        var year = ParseOptionalInt(yearMatch, "year");
        if (yearMatch.Success)
            normalized = RemoveMatch(normalized, yearMatch);

        var special = DetectSpecial(normalized);
        normalized = RemoveSpecialTokens(normalized);

        int? absoluteEpisode = null;
        if (!episodeStart.HasValue && mediaType is VideoLibraryMediaType.Anime or VideoLibraryMediaType.Auto)
        {
            var absoluteMatch = AbsoluteEpisodePattern.Match(normalized);
            var candidate = ParseOptionalInt(absoluteMatch, "absolute");
            if (candidate is > 0 and < 1900)
            {
                absoluteEpisode = candidate;
                episodeStart = candidate;
                episodeEnd = candidate;
                normalized = RemoveMatch(normalized, absoluteMatch);
            }
        }

        var removedTags = ImmutableArray.CreateBuilder<string>();
        var leadingBracket = LeadingBracketPattern.Match(normalized);
        if (leadingBracket.Success && parsedEpisodeEvidence())
        {
            var hasReleaseMetadataBracket = BracketPattern.Matches(normalized)
                .Cast<Match>()
                .Any(match => match.Index != leadingBracket.Index
                              && IsReleaseTag(CollapseSeparators(match.Groups["value"].Value).Trim()));
            if (hasReleaseMetadataBracket)
            {
                removedTags.Add(leadingBracket.Groups["value"].Value.Trim());
                normalized = RemoveMatch(normalized, leadingBracket);
            }
        }
        normalized = BracketPattern.Replace(normalized, match =>
        {
            var token = CollapseSeparators(match.Groups["value"].Value).Trim();
            if (!IsReleaseTag(token))
                return match.Value;
            removedTags.Add(token);
            return " ";
        });

        foreach (var token in ReleaseTokens.OrderByDescending(value => value.Length))
        {
            var tokenPattern = $@"(?<![A-Za-z0-9]){Regex.Escape(token)}(?![A-Za-z0-9])";
            if (!Regex.IsMatch(normalized, tokenPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                continue;
            normalized = Regex.Replace(
                normalized,
                tokenPattern,
                " ",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            removedTags.Add(token);
        }

        var title = CollapseSeparators(normalized).Trim(' ', '.', '-', '_');
        if (title.Length == 0)
            title = original.Normalize(NormalizationForm.FormKC).Trim();

        string? folderTitle = null;
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var fullRoot = string.IsNullOrWhiteSpace(sourceRoot) ? null : Path.GetFullPath(sourceRoot);
            if (fullRoot == null || !string.Equals(directory, fullRoot, StringComparison.OrdinalIgnoreCase))
                folderTitle = new DirectoryInfo(directory).Name.Normalize(NormalizationForm.FormKC);
        }

        return new ParsedVideoIdentity(
            original,
            title,
            folderTitle,
            year,
            season,
            episodeStart,
            episodeEnd,
            absoluteEpisode,
            seriesPart,
            cour,
            special,
            episodeStart.HasValue && episodeEnd.HasValue && episodeStart != episodeEnd,
            episodeStart.HasValue || absoluteEpisode.HasValue || special != ParsedVideoSpecialKind.None,
            externalIds.ToImmutable(),
            removedTags.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray());

        bool parsedEpisodeEvidence() =>
            episodeStart.HasValue || absoluteEpisode.HasValue || special != ParsedVideoSpecialKind.None;
    }

    private static Match FirstSuccessfulMatch(string value, params Regex[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = pattern.Match(value);
            if (match.Success)
                return match;
        }
        return Match.Empty;
    }

    private static int? ParseOptionalInt(Match match, string groupName) =>
        match.Success
        && match.Groups[groupName] is { Success: true } group
        && int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static string RemoveMatch(string value, Match match) =>
        value.Remove(match.Index, match.Length).Insert(match.Index, " ");

    private static string CollapseSeparators(string value) => SeparatorPattern.Replace(value, " ");

    private static string NormalizeProviderId(string value) => value.ToLowerInvariant() switch
    {
        "tmdbid" => "tmdb",
        "tvdbid" => "tvdb",
        "anidbid" => "anidb",
        "anilistid" => "anilist",
        "malid" => "mal",
        "bgmid" or "bangumiid" => "bangumi",
        _ => value.ToLowerInvariant(),
    };

    private static bool IsReleaseTag(string value)
    {
        if (ReleaseTokens.Contains(value))
            return true;
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return tokens.Length > 0 && tokens.All(IsReleaseComponent);
    }

    private static bool IsReleaseComponent(string value) =>
        ReleaseTokens.Contains(value)
        || Regex.IsMatch(value, @"^\d{3,4}x\d{3,4}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        || Regex.IsMatch(value, @"^subs?(?:\([^)]*\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static ParsedVideoSpecialKind DetectSpecial(string value)
    {
        if (Regex.IsMatch(value, @"(?<![A-Za-z0-9])NCOP(?:\d+)?(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
            return ParsedVideoSpecialKind.NcOp;
        if (Regex.IsMatch(value, @"(?<![A-Za-z0-9])NCED(?:\d+)?(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
            return ParsedVideoSpecialKind.NcEd;
        if (Regex.IsMatch(value, @"(?<![A-Za-z0-9])OVA(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
            return ParsedVideoSpecialKind.Ova;
        if (Regex.IsMatch(value, @"(?<![A-Za-z0-9])OAD(?![A-Za-z0-9])", RegexOptions.IgnoreCase))
            return ParsedVideoSpecialKind.Oad;
        if (Regex.IsMatch(value, @"(?<![A-Za-z0-9])SP(?:ECIAL)?\d*(?![A-Za-z0-9])|特別編|特別篇", RegexOptions.IgnoreCase))
            return ParsedVideoSpecialKind.Special;
        return ParsedVideoSpecialKind.None;
    }

    private static string RemoveSpecialTokens(string value) => Regex.Replace(
        value,
        @"(?<![A-Za-z0-9])(?:NCOP|NCED|OVA|OAD|SP(?:ECIAL)?)\d*(?![A-Za-z0-9])|特別編|特別篇",
        " ",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
