using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using FluentAssertions;
using Hoshi.Models.Settings;
using Hoshi.Services.Settings;
using Hoshi.Tests.TestUtils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hoshi.Tests.Services.Settings;

public sealed class SettingsServiceTests
{
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
