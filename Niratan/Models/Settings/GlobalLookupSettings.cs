namespace Niratan.Models.Settings;

public sealed class GlobalLookupSettings
{
    public const string DefaultHotKey = "Ctrl+Alt+D";

    public bool Enabled { get; set; }
    public string HotKey { get; set; } = DefaultHotKey;
}
