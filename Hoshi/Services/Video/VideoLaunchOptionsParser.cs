using System;
using System.Collections.Generic;
using System.Linq;

namespace Hoshi.Services.Video;

public sealed record VideoLaunchOptions(string VideoPath, string? SubtitlePath);

internal static class VideoLaunchOptionsParser
{
    public static VideoLaunchOptions? Parse(string? arguments)
    {
        var tokens = Tokenize(arguments);
        return Parse(tokens);
    }

    public static VideoLaunchOptions? Parse(IEnumerable<string> arguments)
    {
        var tokens = arguments
            .Where(argument => !string.IsNullOrWhiteSpace(argument))
            .ToArray();
        if (tokens.Length == 0)
            return null;

        string? videoPath = null;
        string? subtitlePath = null;

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (IsOption(token, "--open-video", "-v"))
            {
                videoPath = ReadValue(tokens, ref i);
                continue;
            }

            if (IsOption(token, "--subtitle", "-s"))
            {
                subtitlePath = ReadValue(tokens, ref i);
                continue;
            }

            if (!token.StartsWith("-", StringComparison.Ordinal) && videoPath == null)
                videoPath = token;
        }

        return string.IsNullOrWhiteSpace(videoPath)
            ? null
            : new VideoLaunchOptions(videoPath, subtitlePath);
    }

    internal static IReadOnlyList<string> Tokenize(string? arguments)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(arguments))
            return result;

        var current = new List<char>();
        var inQuotes = false;

        foreach (var ch in arguments)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                FlushToken(result, current);
                continue;
            }

            current.Add(ch);
        }

        FlushToken(result, current);
        return result;
    }

    private static string? ReadValue(IReadOnlyList<string> tokens, ref int index)
    {
        if (index + 1 >= tokens.Count)
            return null;

        var start = index + 1;
        var end = start;
        while (end < tokens.Count && !IsKnownOption(tokens[end]))
            end++;

        index = end - 1;
        return end == start
            ? null
            : string.Join(' ', tokens.Skip(start).Take(end - start));
    }

    private static bool IsKnownOption(string value) =>
        IsOption(value, "--open-video", "-v")
        || IsOption(value, "--subtitle", "-s");

    private static bool IsOption(string value, string longName, string shortName) =>
        string.Equals(value, longName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, shortName, StringComparison.OrdinalIgnoreCase);

    private static void FlushToken(ICollection<string> result, ICollection<char> current)
    {
        if (current.Count == 0)
            return;

        var token = new char[current.Count];
        current.CopyTo(token, 0);
        result.Add(new string(token));
        current.Clear();
    }
}
