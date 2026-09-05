using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Niratan.Models.Settings;
using Niratan.Services.Settings;
using Niratan.Tests.TestUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Niratan.Tests.Services.Settings;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task LoadAsync_MigratesLegacyArtworkPoliciesAndPersistsVersion()
    {
        using var temporaryDirectory = new TempDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "VideoSettings": {
                "Metadata": {
                  "OnlineConsentAccepted": true,
                  "ArtworkEnabled": { "anilist": false, "tmdb": true }
                }
              }
            }
            """, TestContext.Current.CancellationToken);
        var service = CreateSut(
            settingsPath,
            (path, json) => File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken));

        await service.LoadAsync();

        service.Current.VideoSettings.Metadata.ArtworkEnabled["anilist"].Should().BeTrue();
        service.Current.VideoSettings.Metadata.ArtworkEnabled["anidb"].Should().BeTrue();
        service.Current.VideoSettings.Metadata.ArtworkPolicyVersion.Should()
            .Be(VideoMetadataSettings.CurrentArtworkPolicyVersion);
        using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(
            settingsPath,
            TestContext.Current.CancellationToken));
        persisted.RootElement.GetProperty(nameof(AppSettings.VideoSettings))
            .GetProperty(nameof(VideoSettings.Metadata))
            .GetProperty(nameof(VideoMetadataSettings.ArtworkPolicyVersion))
            .GetInt32().Should().Be(VideoMetadataSettings.CurrentArtworkPolicyVersion);
    }

    [Fact]
    public async Task LoadAsync_MigratesVersionOneAniDbArtworkWithoutReapplyingOlderPolicies()
    {
        using var temporaryDirectory = new TempDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, """
            {
              "VideoSettings": {
                "Metadata": {
                  "ArtworkPolicyVersion": 1,
                  "ArtworkEnabled": { "anilist": false, "anidb": false }
                }
              }
            }
            """, TestContext.Current.CancellationToken);
        var service = CreateSut(
            settingsPath,
            (path, json) => File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken));

        await service.LoadAsync();

        service.Current.VideoSettings.Metadata.ArtworkEnabled["anilist"].Should().BeFalse();
        service.Current.VideoSettings.Metadata.ArtworkEnabled["anidb"].Should().BeTrue();
        service.Current.VideoSettings.Metadata.ArtworkPolicyVersion.Should()
            .Be(VideoMetadataSettings.CurrentArtworkPolicyVersion);
    }

    [Fact]
    public async Task LoadAsync_CurrentPolicyPreservesExplicitlyDisabledAniDbArtwork()
    {
        using var temporaryDirectory = new TempDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        await File.WriteAllTextAsync(settingsPath, $$"""
            {
              "VideoSettings": {
                "Metadata": {
                  "ArtworkPolicyVersion": {{VideoMetadataSettings.CurrentArtworkPolicyVersion}},
                  "ArtworkEnabled": { "anidb": false }
                }
              }
            }
            """, TestContext.Current.CancellationToken);
        var writeCount = 0;
        var service = CreateSut(
            settingsPath,
            (path, json) =>
            {
                writeCount++;
                return File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);
            });

        await service.LoadAsync();

        service.Current.VideoSettings.Metadata.ArtworkEnabled["anidb"].Should().BeFalse();
        writeCount.Should().Be(0, "current-policy user choices must not be rewritten by migration");
    }

    [Fact]
    public void NewMetadataSettings_EnableAniDbArtworkByDefault()
    {
        var metadata = new VideoMetadataSettings();

        metadata.ArtworkEnabled["anidb"].Should().BeTrue();
        metadata.ArtworkPolicyVersion.Should().Be(VideoMetadataSettings.CurrentArtworkPolicyVersion);
    }

    [Fact]
    public async Task MonoTorrentSettings_round_trip_trackers_and_connection_options()
    {
        using var temporaryDirectory = new TempDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        var service = CreateSut(
            settingsPath,
            (path, json) => File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken));
        service.Set(settings => settings.MonoTorrentSettings, new MonoTorrentSettings
        {
            DownloadRootPath = Path.Combine(temporaryDirectory.Path, "downloads"),
            AdditionalTrackers = ["udp://tracker.example:6969/announce"],
            ListenPort = 51413,
            EnableDht = false,
            MaximumConnections = 240,
            DownloadRateLimitKiB = 8192,
        });

        await service.SaveAsync();
        var reloaded = CreateSut(
            settingsPath,
            (path, json) => File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken));
        await reloaded.LoadAsync();

        reloaded.Current.MonoTorrentSettings.AdditionalTrackers.Should()
            .Equal("udp://tracker.example:6969/announce");
        reloaded.Current.MonoTorrentSettings.DownloadRootPath.Should()
            .Be(Path.Combine(temporaryDirectory.Path, "downloads"));
        reloaded.Current.MonoTorrentSettings.ListenPort.Should().Be(51413);
        reloaded.Current.MonoTorrentSettings.EnableDht.Should().BeFalse();
        reloaded.Current.MonoTorrentSettings.MaximumConnections.Should().Be(240);
        reloaded.Current.MonoTorrentSettings.DownloadRateLimitKiB.Should().Be(8192);
    }

    [Fact]
    public async Task ConcurrentSaves_AreSerializedAndPersistTheLatestSettings()
    {
        using var temporaryDirectory = new TempDirectory();
        var settingsPath = Path.Combine(temporaryDirectory.Path, "settings.json");
        var firstWriteStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowWritesToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var synchronization = new object();
        var activeWrites = 0;
        var maximumConcurrentWrites = 0;
        var writeCount = 0;
        var service = CreateSut(
            settingsPath,
            async (path, json) =>
            {
                lock (synchronization)
                {
                    activeWrites++;
                    maximumConcurrentWrites = Math.Max(maximumConcurrentWrites, activeWrites);
                    writeCount++;
                }

                firstWriteStarted.TrySetResult();
                await allowWritesToComplete.Task;
                await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken);

                lock (synchronization)
                    activeWrites--;
            });

        service.Set(settings => settings.VideoSettings, new VideoSettings { SubtitleFontSize = 41 });
        var oldPlayerSave = service.SaveAsync();
        await firstWriteStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        service.Set(settings => settings.VideoSettings, new VideoSettings { SubtitleFontSize = 58 });
        var newPlayerSave = service.SaveAsync();

        lock (synchronization)
        {
            writeCount.Should().Be(1, "the new player must wait for the old player's shared settings write");
            maximumConcurrentWrites.Should().Be(1);
        }

        allowWritesToComplete.TrySetResult();
        await Task.WhenAll(oldPlayerSave, newPlayerSave);

        lock (synchronization)
            maximumConcurrentWrites.Should().Be(1);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(
            settingsPath,
            TestContext.Current.CancellationToken));
        document.RootElement
            .GetProperty(nameof(AppSettings.VideoSettings))
            .GetProperty(nameof(VideoSettings.SubtitleFontSize))
            .GetDouble()
            .Should()
            .Be(58);
    }

    private static SettingsService CreateSut(string settingsPath, Func<string, string, Task> writeAllTextAsync) =>
        new(
            NullLogger<SettingsService>.Instance,
            settingsPath,
            writeAllTextAsync);
}
