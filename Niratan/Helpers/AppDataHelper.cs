using System;
using System.IO;

namespace Niratan.Helpers;

public static class AppDataHelper
{
    private const string CurrentAppDataDirectoryName = "Niratan";
    private const string LegacyAppDataDirectoryName = "Hoshi";
    private const string CurrentDatabaseFileName = "niratan.db";
    private const string LegacyDatabaseFileName = "hoshi.db";

    private static readonly object MigrationGate = new();
    private static bool _migrationChecked;

    private static readonly string _roamingAppDataPath =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly string _localAppDataPath =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string _appDataPath = Path.Combine(
        _roamingAppDataPath,
        CurrentAppDataDirectoryName
    );

    private static readonly string _legacyAppDataPath = Path.Combine(
        _roamingAppDataPath,
        LegacyAppDataDirectoryName
    );

    public static string GetAppDataPath()
    {
        EnsureLegacyAppDataMigrated();
        return EnsureDirectory(_appDataPath);
    }

    public static string GetDataPath() => EnsureDirectory(Path.Combine(GetAppDataPath(), "Data"));

    public static string GetTemporaryDataPath() => EnsureDirectory(Path.Combine(
        _localAppDataPath,
        CurrentAppDataDirectoryName,
        "Temp"));

    public static string GetWebView2UserDataPath() =>
        EnsureDirectory(Path.Combine(GetAppDataPath(), "WebView2"));

    public static string GetPluginsPath() =>
        EnsureDirectory(Path.Combine(GetAppDataPath(), "Plugins"));

    public static string GetNovelBooksPath() =>
        EnsureDirectory(Path.Combine(GetDataPath(), "Novels"));

    public static string GetMangaDataPath() =>
        EnsureDirectory(Path.Combine(GetDataPath(), "Manga"));

    public static string GetGameDataPath() =>
        EnsureDirectory(Path.Combine(GetDataPath(), "Games"));

    public static string GetMangaCachePath() =>
        EnsureDirectory(Path.Combine(GetMangaDataPath(), "Cache"));

    public static string GetMangaOcrCachePath() =>
        EnsureDirectory(Path.Combine(GetMangaDataPath(), "OCR"));

    public static string GetSuwayomiConfigurationPath() =>
        Path.Combine(GetMangaDataPath(), "suwayomi.json");

    public static string GetMihonConfigurationPath() =>
        Path.Combine(GetMangaDataPath(), "mihon.json");

    public static string GetMihonExtensionsPath() =>
        EnsureDirectory(Path.Combine(GetMangaDataPath(), "Extensions"));

    public static string GetMihonInstalledExtensionsPath() =>
        Path.Combine(GetMihonExtensionsPath(), "installed.json");

    public static string GetMihonBridgeDataPath() =>
        EnsureDirectory(Path.Combine(GetMangaDataPath(), "MihonBridge"));

    public static string GetMangaCatalogPath() =>
        Path.Combine(GetMangaDataPath(), "catalog.json");

    public static string GetGoogleDriveCoverCachePath() =>
        EnsureDirectory(Path.Combine(GetAppDataPath(), "Cache", "GoogleDriveCovers"));

    public static string GetVideoMetadataArtworkCachePath() =>
        EnsureDirectory(Path.Combine(GetAppDataPath(), "Cache", "VideoMetadataArtwork"));

    public static string GetNovelBookPath(string bookId) =>
        EnsureDirectory(Path.Combine(GetNovelBooksPath(), bookId));

    public static string CopyDllToPluginsDirectory(string sourceDllPath)
    {
        string fileName = Path.GetFileName(sourceDllPath);
        string destPath = Path.Combine(GetPluginsPath(), fileName);
        File.Copy(sourceDllPath, destPath, true);
        return destPath;
    }

    internal static void MigrateLegacyAppData(string legacyPath, string currentPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);

        var legacyFullPath = Path.GetFullPath(legacyPath);
        var currentFullPath = Path.GetFullPath(currentPath);
        if (string.Equals(legacyFullPath, currentFullPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Legacy and current app-data paths must be different.");

        if (!Directory.Exists(currentFullPath) && Directory.Exists(legacyFullPath))
            Directory.Move(legacyFullPath, currentFullPath);

        if (!Directory.Exists(currentFullPath))
            return;

        var dataPath = Path.Combine(currentFullPath, "Data");
        var legacyDatabasePath = Path.Combine(dataPath, LegacyDatabaseFileName);
        var currentDatabasePath = Path.Combine(dataPath, CurrentDatabaseFileName);
        if (File.Exists(legacyDatabasePath) && !File.Exists(currentDatabasePath))
            File.Move(legacyDatabasePath, currentDatabasePath);
    }

    private static void EnsureLegacyAppDataMigrated()
    {
        lock (MigrationGate)
        {
            if (_migrationChecked)
                return;

            MigrateLegacyAppData(_legacyAppDataPath, _appDataPath);
            _migrationChecked = true;
        }
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
