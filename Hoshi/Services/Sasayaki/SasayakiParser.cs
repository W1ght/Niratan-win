using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Hoshi.Models.Sasayaki;

namespace Hoshi.Services.Sasayaki;

public sealed class SasayakiParser
{
    public async Task<List<SasayakiCue>> ParseAsync(string srtFilePath)
    {
        var text = await File.ReadAllTextAsync(srtFilePath);
        return Parse(text);
    }

    public List<SasayakiCue> Parse(string srtContent)
    {
        var cues = new List<SasayakiCue>();
        var normalizedContent = srtContent
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var blocks = Regex.Split(normalizedContent.Trim(), @"\n\s*\n");

        foreach (var block in blocks)
        {
            var trimmed = block.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            var lines = trimmed.Split('\n');
            if (lines.Length < 3)
                continue;

            if (!int.TryParse(lines[0].Trim().Trim('\uFEFF'), out var id))
                continue;

            var timestampLine = lines[1].Trim();
            var timestampParts = timestampLine.Split("-->");
            if (timestampParts.Length != 2)
                continue;

            var startTime = ParseTimestamp(timestampParts[0].Trim());
            var endTime = ParseTimestamp(timestampParts[1].Trim());

            var cueText = string.Join("\n", lines[2..]);

            cues.Add(new SasayakiCue
            {
                Id = id,
                StartTime = startTime,
                EndTime = endTime,
                Text = cueText,
            });
        }

        return cues;
    }

    private static double ParseTimestamp(string timestamp)
    {
        // Format: HH:MM:SS,mmm or HH:MM:SS.mmm
        var clean = timestamp.Replace(',', '.');
        var parts = clean.Split(':');
        if (parts.Length != 3)
            return 0;

        var hours = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var minutes = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var seconds = double.Parse(parts[2], CultureInfo.InvariantCulture);

        return hours * 3600 + minutes * 60 + seconds;
    }
}
