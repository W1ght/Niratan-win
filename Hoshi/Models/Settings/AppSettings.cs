using Hoshi.Enums;

namespace Hoshi.Models.Settings;

public class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string ReaderFontFamily { get; set; } = "system-ui, sans-serif";
    public WindowState MainWindowState { get; set; } = new();
}
