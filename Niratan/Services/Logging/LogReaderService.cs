using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models;

namespace Niratan.Services.Logging;

public sealed partial class LogReaderService : ILogReaderService
{
    private static string LogsDir => Path.Combine(AppDataHelper.GetAppDataPath(), "Logs");

    public Task<List<LogEntry>> ReadRecentLogsAsync(int maxEntries = 500)
    {
        return Task.FromResult(ReadRecentLogs(maxEntries));
    }

    public Task<List<LogEntry>> ReadErrorLogsAsync(int maxEntries = 200)
    {
        var all = ReadRecentLogs(maxEntries * 3);
        var errors = all
            .Where(e => e.IsError || e.IsWarning)
            .Take(maxEntries)
            .ToList();
        return Task.FromResult(errors);
    }

    private static List<LogEntry> ReadRecentLogs(int maxEntries)
    {
        var entries = new List<LogEntry>();

        if (!Directory.Exists(LogsDir))
            return entries;

        var logFiles = Directory.GetFiles(LogsDir, "niratan-*.log")
            .Concat(Directory.GetFiles(LogsDir, "hoshi-*.log"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f)
            .ToList();

        foreach (var filePath in logFiles)
        {
            if (entries.Count >= maxEntries)
                break;

            try
            {
                ReadLogFile(filePath, entries, maxEntries);
            }
            catch
            {
                // Skip unreadable files
            }
        }

        // Sort descending (newest first) for display
        entries.Sort((a, b) => b.Timestamp.CompareTo(a.Timestamp));
        return entries;
    }

    private static void ReadLogFile(string filePath, List<LogEntry> entries, int maxEntries)
    {
        var lines = File.ReadAllLines(filePath);

        for (var i = lines.Length - 1; i >= 0; i--)
        {
            if (entries.Count >= maxEntries)
                return;

            var line = lines[i];
            var match = LogLineRegex().Match(line);
            if (!match.Success)
                continue;

            var tsStr = match.Groups["ts"].Value;
            var level = match.Groups["lvl"].Value;
            var context = match.Groups["ctx"].Value;
            var message = match.Groups["msg"].Value;

            if (!DateTime.TryParseExact(tsStr, "yyyy-MM-dd HH:mm:ss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                continue;

            entries.Add(new LogEntry(timestamp, level, context, message));
        }
    }

    [GeneratedRegex(
        @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) \[(?<lvl>[A-Z]{3})\] \((?<ctx>[^)]+)\) (?<msg>.+)$",
        RegexOptions.None)]
    private static partial Regex LogLineRegex();
}
