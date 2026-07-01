using Hoshi.Enums;
using Hoshi.Models.Novel;
using Hoshi.Models.Sasayaki;

namespace Hoshi.Models.Settings;

public class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string ReaderFontFamily { get; set; } = "system-ui, sans-serif";
    public WindowState MainWindowState { get; set; } = new();
    public DictionaryDisplaySettings DictionaryDisplaySettings { get; set; } = new();
    public AudioSettings AudioSettings { get; set; } = new();
    public AnkiSettings AnkiSettings { get; set; } = new();
    public NovelLibrarySortOption NovelLibrarySortOption { get; set; } = NovelLibrarySortOption.Recent;
    public SasayakiSettings SasayakiSettings { get; set; } = new();
    public NovelStatisticsSettings StatisticsSettings { get; set; } = new();
}
