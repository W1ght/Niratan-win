using System.Collections.Generic;
using System.Threading.Tasks;
using Hoshi.Models;

namespace Hoshi.Services.Logging;

public interface ILogReaderService
{
    Task<List<LogEntry>> ReadRecentLogsAsync(int maxEntries = 500);
    Task<List<LogEntry>> ReadErrorLogsAsync(int maxEntries = 200);
}
