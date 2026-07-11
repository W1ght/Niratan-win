using System;
using System.IO;

namespace Hoshi.Helpers;

public static class AppDataHelper
{
    private static readonly string _appDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Hoshi"
    );

    public static string GetAppDataPath() => EnsureDirectory(_appDataPath);

    public static string GetDataPath() => EnsureDirectory(Path.Combine(GetAppDataPath(), "Data"));

    public static string GetWebView2UserDataPath() =>
        EnsureDirectory(Path.Combine(GetAppDataPath(), "WebView2"));

    public static string GetPluginsPath() =>
        EnsureDirectory(Path.Combine(GetAppDataPath(), "Plugins"));

    public static string GetNovelBooksPath() =>
        EnsureDirectory(Path.Combine(GetDataPath(), "Novels"));

    public static string GetNovelBookPath(string bookId) =>
        EnsureDirectory(Path.Combine(GetNovelBooksPath(), bookId));

    public static string CopyDllToPluginsDirectory(string sourceDllPath)
    {
        string fileName = Path.GetFileName(sourceDllPath);
        string destPath = Path.Combine(GetPluginsPath(), fileName);
        File.Copy(sourceDllPath, destPath, true);
        return destPath;
    }

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
