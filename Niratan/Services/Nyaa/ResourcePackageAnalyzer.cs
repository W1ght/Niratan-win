using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Niratan.Models.Nyaa;

namespace Niratan.Services.Nyaa;

public sealed partial class ResourcePackageAnalyzer
{
    private static readonly HashSet<string> EpubExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".epub" };

    private static readonly HashSet<string> AudioExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".m4b", ".m4a", ".aac", ".flac", ".wav", ".ogg", ".opus",
        };

    private static readonly HashSet<string> SubtitleExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".srt", ".vtt", ".ass", ".ssa" };

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".webm", ".avi", ".mov" };

    public ResourcePackageAnalysis Analyze(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Resource package folder was not found: {root}");

        var files = EnumerateSafeFiles(root).ToList();
        var epubs = SelectByExtension(files, EpubExtensions);
        var audio = SelectByExtension(files, AudioExtensions);
        var subtitles = SelectByExtension(files, SubtitleExtensions);
        var videos = SelectByExtension(files, VideoExtensions);
        var classified = epubs.Concat(audio).Concat(subtitles).Concat(videos)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var other = files.Where(path => !classified.Contains(path)).ToList();

        var warnings = new List<string>();
        var novelMatch = FindNovelMatch(epubs, audio, subtitles, warnings);
        var videoSubtitleMatches = MatchVideoSubtitles(videos, subtitles);

        return new ResourcePackageAnalysis(
            root,
            epubs,
            audio,
            subtitles,
            videos,
            other,
            novelMatch,
            videoSubtitleMatches,
            warnings);
    }

    private static IEnumerable<string> EnumerateSafeFiles(string root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            var fullPath = Path.GetFullPath(path);
            if (IsWithinRoot(root, fullPath))
                yield return fullPath;
        }
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.Length > 0
            && !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> SelectByExtension(
        IReadOnlyList<string> files,
        HashSet<string> extensions) =>
        files.Where(path => extensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static NovelResourceMatch? FindNovelMatch(
        IReadOnlyList<string> epubs,
        IReadOnlyList<string> audio,
        IReadOnlyList<string> subtitles,
        List<string> warnings)
    {
        var srtFiles = subtitles
            .Where(path => Path.GetExtension(path).Equals(".srt", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (epubs.Count == 0 || audio.Count == 0 || srtFiles.Count == 0)
            return null;

        if (epubs.Count == 1 && audio.Count == 1 && srtFiles.Count == 1)
            return new NovelResourceMatch(epubs[0], audio[0], srtFiles[0], 1);

        var candidates =
            from epub in epubs
            from audiobook in audio
            from subtitle in srtFiles
            let score = ScoreNovelTriple(epub, audiobook, subtitle)
            orderby score descending
            select new NovelResourceMatch(epub, audiobook, subtitle, score);

        var ranked = candidates.Take(2).ToList();
        if (ranked.Count == 0 || ranked[0].Confidence < 0.72)
        {
            warnings.Add("Multiple novel resources were found, but no high-confidence EPUB/audio/subtitle match was available.");
            return null;
        }

        if (ranked.Count > 1 && ranked[0].Confidence - ranked[1].Confidence < 0.12)
        {
            warnings.Add("Multiple novel resource matches were similarly likely; automatic Sasayaki matching was skipped.");
            return null;
        }

        return ranked[0];
    }

    private static double ScoreNovelTriple(string epub, string audio, string subtitle)
    {
        var epubKey = NormalizeStem(epub);
        var audioKey = NormalizeStem(audio);
        var subtitleKey = NormalizeStem(subtitle);
        var titleScore = (
            Similarity(epubKey, audioKey)
            + Similarity(epubKey, subtitleKey)
            + Similarity(audioKey, subtitleKey)) / 3d;
        var directoryScore = SameDirectory(audio, subtitle) ? 0.12 : 0;
        return Math.Min(1, titleScore * 0.88 + directoryScore);
    }

    private static IReadOnlyDictionary<string, string> MatchVideoSubtitles(
        IReadOnlyList<string> videos,
        IReadOnlyList<string> subtitles)
    {
        var matches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in videos)
        {
            var sameDirectory = subtitles.Where(path => SameDirectory(path, video)).ToList();
            var videoStem = Path.GetFileNameWithoutExtension(video);
            var exact = sameDirectory.FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    videoStem,
                    StringComparison.OrdinalIgnoreCase));
            var languageSuffix = sameDirectory.FirstOrDefault(path =>
                Path.GetFileNameWithoutExtension(path)
                    .StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase));
            var best = exact ?? languageSuffix;
            if (best is not null)
                matches[video] = best;
        }

        return matches;
    }

    private static bool SameDirectory(string left, string right) =>
        string.Equals(
            Path.GetDirectoryName(left),
            Path.GetDirectoryName(right),
            StringComparison.OrdinalIgnoreCase);

    internal static string NormalizeStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        stem = BracketedTextRegex().Replace(stem, " ");
        stem = NoiseTokenRegex().Replace(stem, " ");
        stem = NonLetterOrDigitRegex().Replace(stem, " ");
        return WhitespaceRegex().Replace(stem, " ").Trim().ToUpperInvariant();
    }

    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
            return 0;
        if (string.Equals(left, right, StringComparison.Ordinal))
            return 1;
        if (left.Contains(right, StringComparison.Ordinal)
            || right.Contains(left, StringComparison.Ordinal))
        {
            return (double)Math.Min(left.Length, right.Length) / Math.Max(left.Length, right.Length);
        }

        var leftTokens = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var rightTokens = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var union = leftTokens.Union(rightTokens).Count();
        return union == 0 ? 0 : (double)leftTokens.Intersect(rightTokens).Count() / union;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)|\{[^\}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedTextRegex();

    [GeneratedRegex(
        @"\b(EPUB|AUDIOBOOK|AUDIO|BOOK|SRT|SUBS?|JPN|JAPANESE|1080P|720P|2160P|4K|HEVC|H26[45]|X26[45]|AAC|FLAC|WEB[- ]?DL|BLU[- ]?RAY)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoiseTokenRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonLetterOrDigitRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}
