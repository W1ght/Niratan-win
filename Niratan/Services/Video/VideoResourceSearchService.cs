using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Niratan.Models.Common;
using Niratan.Models.Nyaa;
using Niratan.Models.Video;
using Niratan.Services.Nyaa;

namespace Niratan.Services.Video;

internal sealed class VideoResourceSearchService : IVideoResourceSearchService
{
    private readonly INyaaClient _nyaa;

    public VideoResourceSearchService(INyaaClient nyaa) => _nyaa = nyaa;

    public string BuildDefaultQuery(VideoMetadataCandidate identity)
    {
        var title = PreferredSearchTerms(identity).FirstOrDefault() ?? identity.Title;
        return AppendIdentitySuffix(title, identity);
    }

    public string BuildSubtitleQuery(VideoMetadataCandidate identity)
    {
        var query = BuildDefaultQuery(identity);
        return query.Contains("srt", StringComparison.OrdinalIgnoreCase)
            || query.Contains("subtitle", StringComparison.OrdinalIgnoreCase)
            || query.Contains("字幕", StringComparison.Ordinal)
            ? query
            : $"{query} srt";
    }

    public IReadOnlyList<string> BuildSearchQueries(VideoMetadataCandidate identity, string? requestedQuery = null)
    {
        if (!string.IsNullOrWhiteSpace(requestedQuery))
            return [requestedQuery.Trim()];

        return PreferredSearchTerms(identity)
            .Select(term => AppendIdentitySuffix(term, identity))
            .Where(query => query.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static IEnumerable<string> PreferredSearchTerms(VideoMetadataCandidate identity)
    {
        // Nyaa titles are normally English or romanized. Put those before a local-language
        // title, while still retaining aliases supplied by AniList/Bangumi/TMDB.
        var terms = new[] { identity.OriginalTitle }
            .Concat(identity.Aliases.IsDefault ? [] : identity.Aliases)
            .Concat([identity.Title]);
        return terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => term!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select((term, index) => (term, index))
            .OrderByDescending(item => IsNyaaFriendlyTitle(item.term))
            .ThenBy(item => item.index)
            .Select(item => item.term);
    }

    private static bool IsNyaaFriendlyTitle(string value) =>
        value.Any(char.IsLetter)
        && value.All(character =>
            !char.IsLetter(character)
            || character <= '\u024F'
            || character is >= '\u1E00' and <= '\u1EFF');

    private static string AppendIdentitySuffix(string title, VideoMetadataCandidate identity)
    {
        var suffix = identity.Year is int year ? " " + year : string.Empty;
        if (identity.SeasonNumber is int season)
            suffix += $" S{season:00}";
        if (identity.EpisodeNumber is int episode)
            suffix += $" E{episode:00}";
        return (title.Trim() + suffix).Trim();
    }

    public Task<Result<IReadOnlyList<NyaaTorrentItem>>> SearchAsync(
        VideoResourceSearchRequest request,
        CancellationToken ct = default)
    {
        var queries = BuildSearchQueries(request.Identity, request.Query);
        if (queries.Count == 0)
            return Task.FromResult(Result<IReadOnlyList<NyaaTorrentItem>>.Failure(
                "Enter a title before searching Nyaa.", "Nyaa search"));

        return SearchQueriesAsync(queries, request.CategoryCode, ct);
    }

    private async Task<Result<IReadOnlyList<NyaaTorrentItem>>> SearchQueriesAsync(
        IReadOnlyList<string> queries,
        string categoryCode,
        CancellationToken ct)
    {
        var results = await Task.WhenAll(queries.Select(query => SearchOneAsync(query, categoryCode, ct)));
        if (results.Any(result => result.IsCancelled))
            return Result<IReadOnlyList<NyaaTorrentItem>>.Cancelled();

        var successful = results.Where(result => result.IsSuccess && result.Value is not null).ToList();
        if (successful.Count == 0)
        {
            var failure = results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.Error));
            return Result<IReadOnlyList<NyaaTorrentItem>>.Failure(
                failure?.Error ?? "Nyaa search failed.",
                failure?.ErrorTitle ?? "Nyaa search");
        }

        var merged = successful
            .SelectMany(result => result.Value!)
            .GroupBy(item => item.TorrentUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(item => item.Seeders)
            .ThenByDescending(item => item.PublishedAt)
            .ToList();
        return Result<IReadOnlyList<NyaaTorrentItem>>.Success(merged);
    }

    private async Task<Result<IReadOnlyList<NyaaTorrentItem>>> SearchOneAsync(
        string query,
        string categoryCode,
        CancellationToken ct)
    {
        try
        {
            return await _nyaa.SearchAsync(new NyaaSearchRequest(query, categoryCode), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Result<IReadOnlyList<NyaaTorrentItem>>.Cancelled();
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<NyaaTorrentItem>>.Failure(ex.Message, "Nyaa search");
        }
    }
}
