using System;

namespace Niratan.Models.Settings;

public enum DictionaryUpdateInterval
{
    Daily,
    Weekly,
    Monthly,
}

public sealed class DictionaryUpdateSettings
{
    public bool UpdateAutomatically { get; set; } = true;

    public DictionaryUpdateInterval Interval { get; set; } = DictionaryUpdateInterval.Weekly;

    public DateTimeOffset? LastUpdate { get; set; }

    public TimeSpan GetInterval() => Interval switch
    {
        DictionaryUpdateInterval.Daily => TimeSpan.FromDays(1),
        DictionaryUpdateInterval.Monthly => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(7),
    };
}
