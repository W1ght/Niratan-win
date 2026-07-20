using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Niratan.Helpers;
using Niratan.Models.Settings;

namespace Niratan.Services.Settings;

internal sealed class ReaderFontService : IReaderFontService
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".woff", ".woff2" };

    public string FontsPath { get; } = Path.Combine(AppDataHelper.GetAppDataPath(), "Fonts");

    public ReaderFontService()
    {
        Directory.CreateDirectory(FontsPath);
    }

    public IReadOnlyList<JapaneseFontOption> GetAvailableFonts()
    {
        Directory.CreateDirectory(FontsPath);
        return JapaneseFontCatalog.Fonts
            .Concat(Directory.EnumerateFiles(FontsPath)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .Select(CreateImportedOption))
            .ToArray();
    }

    public async Task<JapaneseFontOption> ImportAsync(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
            throw new FileNotFoundException("The selected font file no longer exists.", fullSourcePath);

        var extension = Path.GetExtension(fullSourcePath);
        if (!SupportedExtensions.Contains(extension))
            throw new InvalidDataException("Supported font formats are TTF, OTF, WOFF, and WOFF2.");

        Directory.CreateDirectory(FontsPath);
        var baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(fullSourcePath));
        var destinationPath = UniqueDestinationPath(baseName, extension.ToLowerInvariant());
        await using var source = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await source.CopyToAsync(destination);
        return CreateImportedOption(destinationPath);
    }

    public Task DeleteAsync(string importedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importedFileName);
        var fileName = Path.GetFileName(importedFileName);
        if (!string.Equals(fileName, importedFileName, StringComparison.Ordinal))
            throw new InvalidDataException("Invalid imported font name.");

        var fontsRoot = Path.GetFullPath(FontsPath) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(Path.Combine(FontsPath, fileName));
        if (!target.StartsWith(fontsRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Imported font path escapes the font directory.");

        if (File.Exists(target))
            File.Delete(target);
        return Task.CompletedTask;
    }

    private static JapaneseFontOption CreateImportedOption(string path)
    {
        var fileName = Path.GetFileName(path);
        var displayName = Path.GetFileNameWithoutExtension(path);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fileName)))[..12];
        var family = $"NiratanImported{hash}";
        return new JapaneseFontOption(displayName, $"'{family}', serif", family, fileName);
    }

    private string UniqueDestinationPath(string baseName, string extension)
    {
        var candidate = Path.Combine(FontsPath, baseName + extension);
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(FontsPath, $"{baseName}-{suffix}{extension}");
        return candidate;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "imported-font" : sanitized;
    }
}
