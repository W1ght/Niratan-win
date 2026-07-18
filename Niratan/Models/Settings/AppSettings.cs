using Niratan.Enums;
using Niratan.Models.Novel;
using Niratan.Models.GameControllers;
using Niratan.Models.Sasayaki;
using Niratan.Models.Shortcuts;
using Niratan.Models.Sync;

namespace Niratan.Models.Settings;

public class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string ReaderFontFamily { get; set; } = JapaneseFontCatalog.DefaultReaderCssValue;
    public WindowState MainWindowState { get; set; } = new();
    public DictionaryDisplaySettings DictionaryDisplaySettings { get; set; } = new();
    public DictionaryUpdateSettings DictionaryUpdateSettings { get; set; } = new();
    public GlobalLookupSettings GlobalLookup { get; set; } = new();
    public AudioSettings AudioSettings { get; set; } = new();
    public VideoSettings VideoSettings { get; set; } = new();
    public ShortcutConfiguration ShortcutConfiguration { get; set; } = new();
    public GameControllerConfiguration GameControllerConfiguration { get; set; } = new();
    public AnkiSettings AnkiSettings { get; set; } = new();
    public NovelLibrarySortOption NovelLibrarySortOption { get; set; } = NovelLibrarySortOption.Recent;
    public bool BookshelfShowReading { get; set; }
    public SasayakiSettings SasayakiSettings { get; set; } = new();
    public NovelStatisticsSettings StatisticsSettings { get; set; } = new();
    public TtuSyncSettings TtuSyncSettings { get; set; } = new();
}
