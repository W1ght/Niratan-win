using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Niratan.Models.Profiles;
using Niratan.Services.Backup;

namespace Niratan.Tests.Services.Backup;

public sealed class BackupServiceTests
{
    [Fact]
    public async Task BooksBackup_RestoreOverwritesCurrentCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var books = Path.Combine(temp.Path, "Novels");
        var dictionaries = Path.Combine(temp.Path, "dictionaries");
        var profiles = Path.Combine(temp.Path, "Profiles");
        Directory.CreateDirectory(Path.Combine(books, "book-a"));
        await File.WriteAllTextAsync(Path.Combine(books, "book-a", "metadata.json"), "original", ct);
        var service = CreateService(books, dictionaries, profiles);
        var archive = Path.Combine(temp.Path, "Books.hoshi");

        await service.CreateHoshiBackupAsync(HoshiBackupTarget.Books, archive, ct);
        Directory.Delete(Path.Combine(books, "book-a"), recursive: true);
        Directory.CreateDirectory(Path.Combine(books, "book-b"));
        await File.WriteAllTextAsync(Path.Combine(books, "book-b", "metadata.json"), "replacement", ct);

        await service.RestoreHoshiBackupAsync(HoshiBackupTarget.Books, archive, ct);

        File.ReadAllText(Path.Combine(books, "book-a", "metadata.json")).Should().Be("original");
        Directory.Exists(Path.Combine(books, "book-b")).Should().BeFalse();
    }

    [Fact]
    public async Task Restore_RejectsZipSlipWithoutChangingCollection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var books = Path.Combine(temp.Path, "Novels");
        Directory.CreateDirectory(books);
        await File.WriteAllTextAsync(Path.Combine(books, "keep.txt"), "safe", ct);
        var archive = Path.Combine(temp.Path, "unsafe.hoshi");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            zip.CreateEntry("../escaped.txt");
        var service = CreateService(
            books,
            Path.Combine(temp.Path, "dictionaries"),
            Path.Combine(temp.Path, "Profiles"));

        var action = () => service.RestoreHoshiBackupAsync(HoshiBackupTarget.Books, archive, ct);

        await action.Should().ThrowAsync<InvalidDataException>();
        File.ReadAllText(Path.Combine(books, "keep.txt")).Should().Be("safe");
        File.Exists(Path.Combine(temp.Path, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task DictionaryBackup_RestoresCollectionAndMergesProfileDictionarySettings()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempDirectory();
        var books = Path.Combine(temp.Path, "Novels");
        var dictionaries = Path.Combine(temp.Path, "dictionaries");
        var profiles = Path.Combine(temp.Path, "Profiles");
        WriteDictionary(dictionaries, "Original");
        var imported = ProfileIndex.CreateDefault();
        imported.Profiles.Add(new NiratanProfile("custom-imported", "Imported", "ja"));
        await WriteProfilesAsync(profiles, imported, "custom-imported", "imported-config", ct);
        var service = CreateService(books, dictionaries, profiles);
        var archive = Path.Combine(temp.Path, "Dictionaries.hoshi");
        await service.CreateHoshiBackupAsync(HoshiBackupTarget.Dictionaries, archive, ct);

        Directory.Delete(dictionaries, recursive: true);
        WriteDictionary(dictionaries, "Current");
        var current = ProfileIndex.CreateDefault();
        current.Profiles.Add(new NiratanProfile("custom-current", "Current", "ja"));
        await WriteProfilesAsync(profiles, current, "custom-current", "current-config", ct);

        await service.RestoreHoshiBackupAsync(HoshiBackupTarget.Dictionaries, archive, ct);

        Directory.Exists(Path.Combine(dictionaries, "Term", "Original")).Should().BeTrue();
        Directory.Exists(Path.Combine(dictionaries, "Term", "Current")).Should().BeFalse();
        var restoredIndex = JsonSerializer.Deserialize<ProfileIndex>(
            await File.ReadAllTextAsync(Path.Combine(profiles, "profiles.json"), ct))!;
        restoredIndex.Profiles.Select(profile => profile.Id)
            .Should().Contain(["custom-current", "custom-imported"]);
        File.ReadAllText(Path.Combine(
                profiles,
                "custom-imported",
                "dictionaries",
                "dictionary-config.json"))
            .Should().Be("imported-config");
    }

    private static BackupService CreateService(string books, string dictionaries, string profiles) =>
        new(
            NullLogger<BackupService>.Instance,
            books,
            dictionaries,
            profiles);

    private static void WriteDictionary(string root, string name)
    {
        var path = Path.Combine(root, "Term", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "index.json"), "{\"title\":\"test\"}");
    }

    private static async Task WriteProfilesAsync(
        string root,
        ProfileIndex index,
        string profileId,
        string config,
        CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "profiles.json"),
            JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }),
            ct);
        var profileRoot = Path.Combine(root, profileId);
        Directory.CreateDirectory(Path.Combine(profileRoot, "dictionaries"));
        await File.WriteAllTextAsync(
            Path.Combine(profileRoot, "dictionaries", "dictionary-config.json"),
            config,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(profileRoot, "dictionary-settings.json"),
            "{}",
            ct);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
