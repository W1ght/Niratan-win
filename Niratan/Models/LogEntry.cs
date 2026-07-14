using System;

namespace Niratan.Models;

public sealed record LogEntry(
    DateTime Timestamp,
    string Level,
    string SourceContext,
    string Message
)
{
    public Windows.UI.Color LevelColor => Level switch
    {
        "ERR" or "FTL" => Microsoft.UI.Colors.Red,
        "WRN" => Microsoft.UI.Colors.Orange,
        "INF" => Microsoft.UI.Colors.DodgerBlue,
        "DBG" => Microsoft.UI.Colors.Gray,
        _ => Microsoft.UI.Colors.Gray,
    };

    public bool IsError => Level is "ERR" or "FTL";
    public bool IsWarning => Level is "WRN";
    public bool IsInfo => Level is "INF";
    public bool IsDebug => Level is "DBG";
}
