using System.Collections.Generic;
using System.Threading.Tasks;
using Niratan.Models;

namespace Niratan.Services.Logging;

public interface ILogReaderService
{
    Task<List<LogEntry>> ReadRecentLogsAsync(int maxEntries = 500);
    Task<List<LogEntry>> ReadErrorLogsAsync(int maxEntries = 200);
}
