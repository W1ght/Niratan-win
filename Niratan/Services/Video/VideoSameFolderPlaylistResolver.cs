using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Niratan.Models;

namespace Niratan.Services.Video;

public interface IVideoSameFolderPlaylistResolver
{
    IReadOnlyList<VideoItem> Resolve(string selectedPath);
}

internal sealed class VideoSameFolderPlaylistResolver : IVideoSameFolderPlaylistResolver
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".webm", ".m4v", ".wmv", ".flv",
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".ogg", ".opus",
    };

    public IReadOnlyList<VideoItem> Resolve(string selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath) || !File.Exists(selectedPath))
            return [];
        var fullPath = Path.GetFullPath(selectedPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (directory == null)
            return [Create(fullPath)];
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .Select(Path.GetFullPath)
                .OrderBy(path => Path.GetFileName(path), NaturalPathComparer.Instance)
                .Select(Create)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [Create(fullPath)];
        }
    }

    private static VideoItem Create(string path) => new()
    {
        Id = path,
        Title = Path.GetFileNameWithoutExtension(path),
        FilePath = path,
        ImportedAt = File.GetCreationTimeUtc(path),
    };

    private sealed class NaturalPathComparer : IComparer<string>
    {
        public static NaturalPathComparer Instance { get; } = new();

        public int Compare(string? left, string? right)
        {
            left ??= "";
            right ??= "";
            var i = 0;
            var j = 0;
            while (i < left.Length && j < right.Length)
            {
                if (char.IsDigit(left[i]) && char.IsDigit(right[j]))
                {
                    var i0 = i;
                    var j0 = j;
                    while (i < left.Length && char.IsDigit(left[i])) i++;
                    while (j < right.Length && char.IsDigit(right[j])) j++;
                    var a = left.AsSpan(i0, i - i0).TrimStart('0');
                    var b = right.AsSpan(j0, j - j0).TrimStart('0');
                    var length = a.Length.CompareTo(b.Length);
                    if (length != 0) return length;
                    var digits = a.CompareTo(b, StringComparison.Ordinal);
                    if (digits != 0) return digits;
                    continue;
                }
                var comparison = char.ToUpper(left[i], CultureInfo.InvariantCulture)
                    .CompareTo(char.ToUpper(right[j], CultureInfo.InvariantCulture));
                if (comparison != 0) return comparison;
                i++;
                j++;
            }
            return left.Length.CompareTo(right.Length);
        }
    }
}
