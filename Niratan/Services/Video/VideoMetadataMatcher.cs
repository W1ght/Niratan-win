using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Niratan.Models.Video;

namespace Niratan.Services.Video;

public interface IVideoMetadataMatcher
{
    IReadOnlyList<VideoMetadataMatchScore> Score(
        ParsedVideoIdentity parsed,
        VideoMetadataMediaKind mediaKind,
        IReadOnlyList<VideoMetadataCandidate> candidates);
}

internal sealed class VideoMetadataMatcher : IVideoMetadataMatcher
{
    public const double AutomaticThreshold = 0.92;
    public const double RequiredLead = 0.15;

    public IReadOnlyList<VideoMetadataMatchScore> Score(
        ParsedVideoIdentity parsed,
        VideoMetadataMediaKind mediaKind,
        IReadOnlyList<VideoMetadataCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(candidates);

        var directMatches = candidates.Where(candidate => HasExplicitIdMatch(parsed, candidate)).ToList();
        if (directMatches.Count > 0)
        {
            return directMatches
                .Select(candidate => new VideoMetadataMatchScore(
                    candidate,
                    1,
                    1,
                    false,
                    "explicit external id",
                    true,
                    true))
                .Concat(candidates.Except(directMatches).Select(candidate => ScoreCandidate(parsed, mediaKind, candidate)))
                .OrderByDescending(result => result.Score)
                .ToList();
        }

        var scored = candidates
            .Select(candidate => ScoreCandidate(parsed, mediaKind, candidate))
            .OrderByDescending(result => result.Score)
            .ThenBy(result => result.Candidate.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        if (scored.Count == 0)
            return scored;

        var first = scored[0];
        var secondScore = scored.Count > 1 ? scored[1].Score : 0;
        var exactMatches = scored
            .Where(result => result.TitleScore >= 0.999 && !result.HasHardConflict)
            .ToArray();
        var exactYears = exactMatches
            .Where(result => result.Candidate.Year.HasValue)
            .Select(result => result.Candidate.Year!.Value)
            .Distinct()
            .ToArray();
        var jointExact = exactMatches.Length > 1
                         && exactYears.Length <= 1
                         && parsed.EpisodeStart.HasValue
                         && exactMatches.All(result => result.Candidate.MediaKind is
                             VideoMetadataMediaKind.Series or VideoMetadataMediaKind.Anime);
        var uniqueExact = first.TitleScore >= 0.999
                          && (exactMatches.Length == 1 || jointExact);
        var hasEvidence = HasCorroboratingEvidence(parsed, first.Candidate)
                          || first.TitleScore >= 0.999
                          && parsed.EpisodeStart.HasValue
                          && first.Candidate.MediaKind is VideoMetadataMediaKind.Series
                              or VideoMetadataMediaKind.Anime;
        var accepted = !first.HasHardConflict
                       && hasEvidence
                       && (uniqueExact
                           || (first.Score >= AutomaticThreshold
                               && first.Score - secondScore >= RequiredLead));
        scored[0] = first with
        {
            IsAccepted = accepted,
            Evidence = accepted
                ? first.Evidence + (jointExact
                    ? "; exact alias confirmed by multiple providers"
                    : uniqueExact ? "; unique exact alias" : "; high-confidence lead")
                : first.Evidence,
        };
        return scored;
    }

    private static VideoMetadataMatchScore ScoreCandidate(
        ParsedVideoIdentity parsed,
        VideoMetadataMediaKind expectedKind,
        VideoMetadataCandidate candidate)
    {
        var aliases = candidate.Aliases
            .Add(candidate.Title)
            .Add(candidate.OriginalTitle ?? string.Empty)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        var titleScore = aliases.Max(alias => TitleSimilarity(parsed.NormalizedTitle, alias));
        var typeCompatible = IsCompatible(expectedKind, candidate.MediaKind);
        var hardConflict = HasHardConflict(parsed, candidate, typeCompatible);
        var corroboration = HasCorroboratingEvidence(parsed, candidate);
        var score = hardConflict
            ? 0
            : Math.Clamp((titleScore * 0.75) + (typeCompatible ? 0.10 : 0) + (corroboration ? 0.15 : 0), 0, 1);

        var evidence = new List<string>
        {
            $"title={titleScore.ToString("0.000", CultureInfo.InvariantCulture)}",
            typeCompatible ? "type compatible" : "type mismatch",
            corroboration ? "year/episode corroborated" : "missing corroboration",
        };
        if (hardConflict)
            evidence.Add("hard conflict");

        return new VideoMetadataMatchScore(
            candidate,
            score,
            titleScore,
            hardConflict,
            string.Join("; ", evidence),
            false,
            false);
    }

    private static bool HasExplicitIdMatch(ParsedVideoIdentity parsed, VideoMetadataCandidate candidate)
    {
        if (parsed.ExternalIds.TryGetValue(candidate.ProviderId, out var ownId)
            && string.Equals(ownId, candidate.ProviderItemId, StringComparison.OrdinalIgnoreCase))
            return true;
        return parsed.ExternalIds.Any(pair =>
            candidate.ExternalIds.TryGetValue(pair.Key, out var id)
            && string.Equals(pair.Value, id, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasHardConflict(
        ParsedVideoIdentity parsed,
        VideoMetadataCandidate candidate,
        bool typeCompatible)
    {
        if (!typeCompatible)
            return true;
        if (parsed.Year.HasValue && candidate.Year.HasValue && parsed.Year != candidate.Year)
            return true;
        if (parsed.SeasonNumber.HasValue && candidate.SeasonNumber.HasValue
            && parsed.SeasonNumber != candidate.SeasonNumber)
            return true;
        if (parsed.EpisodeStart.HasValue && candidate.EpisodeNumber.HasValue
            && (candidate.EpisodeNumber < parsed.EpisodeStart
                || candidate.EpisodeNumber > (parsed.EpisodeEnd ?? parsed.EpisodeStart)))
            return true;
        if (parsed.AbsoluteEpisodeNumber.HasValue && candidate.AbsoluteEpisodeNumber.HasValue
            && parsed.AbsoluteEpisodeNumber != candidate.AbsoluteEpisodeNumber)
            return true;
        return false;
    }

    private static bool HasCorroboratingEvidence(
        ParsedVideoIdentity parsed,
        VideoMetadataCandidate candidate)
    {
        if (parsed.Year.HasValue && candidate.Year == parsed.Year)
            return true;
        if (parsed.AbsoluteEpisodeNumber.HasValue
            && candidate.AbsoluteEpisodeNumber == parsed.AbsoluteEpisodeNumber)
            return true;
        return parsed.EpisodeStart.HasValue
               && candidate.EpisodeNumber.HasValue
               && candidate.EpisodeNumber >= parsed.EpisodeStart
               && candidate.EpisodeNumber <= (parsed.EpisodeEnd ?? parsed.EpisodeStart)
               && (!parsed.SeasonNumber.HasValue
                   || !candidate.SeasonNumber.HasValue
                   || parsed.SeasonNumber == candidate.SeasonNumber);
    }

    private static bool IsCompatible(VideoMetadataMediaKind expected, VideoMetadataMediaKind actual) =>
        expected == actual
        || expected == VideoMetadataMediaKind.Anime
           && actual is VideoMetadataMediaKind.Series or VideoMetadataMediaKind.Episode
        || actual == VideoMetadataMediaKind.Anime
           && expected is VideoMetadataMediaKind.Series or VideoMetadataMediaKind.Episode;

    internal static double TitleSimilarity(string left, string right)
    {
        var normalizedLeft = NormalizeTitle(left);
        var normalizedRight = NormalizeTitle(right);
        if (normalizedLeft.Length == 0 || normalizedRight.Length == 0)
            return 0;
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal))
            return 1;
        return Math.Max(
            DamerauLevenshteinRatio(normalizedLeft, normalizedRight),
            BigramDice(normalizedLeft, normalizedRight));
    }

    private static string NormalizeTitle(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var builder = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
                builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static double BigramDice(string left, string right)
    {
        if (left.Length < 2 || right.Length < 2)
            return left == right ? 1 : 0;
        var leftBigrams = Bigrams(left);
        var rightBigrams = Bigrams(right);
        var intersection = 0;
        var counts = leftBigrams.GroupBy(value => value).ToDictionary(group => group.Key, group => group.Count());
        foreach (var value in rightBigrams)
        {
            if (!counts.TryGetValue(value, out var count) || count == 0)
                continue;
            intersection++;
            counts[value] = count - 1;
        }
        return 2d * intersection / (leftBigrams.Count + rightBigrams.Count);
    }

    private static List<string> Bigrams(string value)
    {
        var result = new List<string>(Math.Max(0, value.Length - 1));
        for (var index = 0; index < value.Length - 1; index++)
            result.Add(value.Substring(index, 2));
        return result;
    }

    private static double DamerauLevenshteinRatio(string left, string right)
    {
        var rows = left.Length + 1;
        var columns = right.Length + 1;
        var distance = new int[rows, columns];
        for (var row = 0; row < rows; row++)
            distance[row, 0] = row;
        for (var column = 0; column < columns; column++)
            distance[0, column] = column;

        for (var row = 1; row < rows; row++)
        {
            for (var column = 1; column < columns; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                distance[row, column] = Math.Min(
                    Math.Min(distance[row - 1, column] + 1, distance[row, column - 1] + 1),
                    distance[row - 1, column - 1] + cost);
                if (row > 1 && column > 1
                    && left[row - 1] == right[column - 2]
                    && left[row - 2] == right[column - 1])
                {
                    distance[row, column] = Math.Min(distance[row, column], distance[row - 2, column - 2] + cost);
                }
            }
        }

        return 1d - (double)distance[left.Length, right.Length] / Math.Max(left.Length, right.Length);
    }
}

internal static class VideoMetadataMerger
{
    public static VideoMetadataMergeResult Merge(
        IEnumerable<VideoMetadataFieldValue> values)
    {
        var selected = values
            .GroupBy(value => value.Field, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(value => value.IsLocked)
                    .ThenByDescending(value => value.Priority)
                    .ThenByDescending(value => value.UpdatedAt)
                    .First(value => !string.IsNullOrWhiteSpace(value.Value)),
                StringComparer.OrdinalIgnoreCase);
        return new VideoMetadataMergeResult(
            selected,
            selected.Values.Select(value => value.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray());
    }
}
