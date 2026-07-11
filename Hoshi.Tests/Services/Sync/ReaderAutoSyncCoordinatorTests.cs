using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;
using Microsoft.Extensions.Logging;
using Moq;

namespace Hoshi.Tests.Services.Sync;

public sealed class ReaderAutoSyncCoordinatorTests
{
    private static readonly NovelBook Book = new()
    {
        Id = "book-1",
        Title = "Book One",
        ExtractedPath = "C:\\Books\\book-1",
    };

    [Fact]
    public async Task ImportOnOpenAsync_UsesAutoImportOnlyAndStatisticsOptions()
    {
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                new TtuSyncOptions(
                    Direction: TtuSyncDirection.Auto,
                    SyncBookData: true,
                    SyncStatistics: true,
                    StatisticsSyncMode: StatisticsSyncMode.Replace,
                    SyncAudioBook: true,
                    ImportOnly: true),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TtuSyncResult(TtuSyncResultKind.Imported, Book.Title));

        var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

        (await sut.ImportOnOpenAsync(Book, TestContext.Current.CancellationToken))
            .Should().BeTrue();
        sync.VerifyAll();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task ImportOnOpenAsync_SkipsWhenPrerequisiteIsMissing(
        bool globalSync,
        bool autoSync,
        bool credentials)
    {
        var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
        var settings = EnabledSettings();
        settings.TtuSyncSettings.EnableSync = globalSync;
        settings.TtuSyncSettings.EnableAutoSync = autoSync;
        var sut = CreateCoordinator(sync.Object, settings, credentials);

        (await sut.ImportOnOpenAsync(Book, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ImportOnOpenAsync_ReturnsFalseWhenNothingWasImported()
    {
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TtuSyncResult(TtuSyncResultKind.Synced, Book.Title));
        var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

        (await sut.ImportOnOpenAsync(Book, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ImportOnOpenAsync_ContainsRequestedCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                cts.Token))
            .ThrowsAsync(new OperationCanceledException(cts.Token));
        var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

        (await sut.ImportOnOpenAsync(Book, cts.Token)).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(ContainedOpenFailures))]
    public async Task ImportOnOpenAsync_ContainsNetworkAndOAuthFailures(Exception failure)
    {
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

        (await sut.ImportOnOpenAsync(Book, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ScheduleExport_CoalescesChangesIntoOneThirtySecondDelay()
    {
        var delay = new ControlledDelay();
        var exportCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exportCount = 0;
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((NovelBook _, TtuSyncOptions _, CancellationToken _) =>
            {
                Interlocked.Increment(ref exportCount);
                exportCompleted.TrySetResult();
                return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
            });
        var sut = CreateCoordinator(
            sync.Object,
            EnabledSettings(),
            credentials: true,
            delay.DelayAsync);

        sut.ScheduleExport(Book);
        sut.ScheduleExport(Book);

        delay.Requests.Should().ContainSingle();
        delay.Requests[0].Delay.Should().Be(TimeSpan.FromSeconds(30));
        delay.Requests[0].Release();
        await exportCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sut.Cancel();

        exportCount.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleExport_RunsOneFollowUpWithoutConcurrentExportsWhenChangedDuringExport()
    {
        var delay = new ControlledDelay();
        var firstExportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstExportFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exportStateGate = new object();
        var exportOptions = new List<TtuSyncOptions>();
        var activeExports = 0;
        var maximumActiveExports = 0;
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (NovelBook _, TtuSyncOptions options, CancellationToken _) =>
            {
                int invocation;
                lock (exportStateGate)
                {
                    exportOptions.Add(options);
                    invocation = exportOptions.Count;
                    activeExports++;
                    maximumActiveExports = Math.Max(maximumActiveExports, activeExports);
                }

                try
                {
                    if (invocation == 1)
                    {
                        firstExportStarted.TrySetResult();
                        await firstExportFinished.Task;
                    }

                    return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
                }
                finally
                {
                    lock (exportStateGate)
                        activeExports--;
                }
            });
        var sut = CreateCoordinator(
            sync.Object,
            EnabledSettings(),
            credentials: true,
            delay.DelayAsync);

        sut.ScheduleExport(Book);
        sut.ScheduleExport(Book);
        delay.Requests.Should().ContainSingle();
        delay.Requests[0].Release();
        await firstExportStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        sut.ScheduleExport(Book);
        var flushTask = sut.FlushAsync(Book, TestContext.Current.CancellationToken);
        flushTask.IsCompleted.Should().BeFalse();
        firstExportFinished.TrySetResult();
        await flushTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        exportOptions.Should().HaveCount(2);
        exportOptions.Should().OnlyContain(option =>
            option.Direction == TtuSyncDirection.ExportToTtu && !option.ImportOnly);
        maximumActiveExports.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleExport_DropsPendingWorkWhenPrerequisiteDisappearsBeforeDelay()
    {
        var delay = new ControlledDelay();
        var settings = EnabledSettings();
        var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
        var sut = CreateCoordinator(sync.Object, settings, credentials: true, delay.DelayAsync);

        sut.ScheduleExport(Book);
        settings.TtuSyncSettings.EnableAutoSync = false;
        delay.Requests[0].Release();
        await delay.Requests[0].Completion.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await sut.FlushAsync(Book, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScheduleExport_ContainsFailureAndRunsPendingFollowUp()
    {
        var delay = new ControlledDelay();
        var firstExportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failFirstExport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (NovelBook _, TtuSyncOptions _, CancellationToken _) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstExportStarted.TrySetResult();
                    await failFirstExport.Task;
                    throw new HttpRequestException("remote response must not escape");
                }

                return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
            });
        var sut = CreateCoordinator(
            sync.Object,
            EnabledSettings(),
            credentials: true,
            delay.DelayAsync);

        sut.ScheduleExport(Book);
        delay.Requests[0].Release();
        await firstExportStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sut.ScheduleExport(Book);
        var flushTask = sut.FlushAsync(Book, TestContext.Current.CancellationToken);

        failFirstExport.TrySetResult();
        await flushTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        callCount.Should().Be(2);
    }

    [Fact]
    public async Task FlushAsync_CancelsDelayAndAwaitsFinalExport()
    {
        var delay = new ControlledDelay();
        var exportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishExport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                exportStarted.TrySetResult();
                await finishExport.Task;
                return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
            });
        var sut = CreateCoordinator(
            sync.Object,
            EnabledSettings(),
            credentials: true,
            delay.DelayAsync);

        sut.ScheduleExport(Book);
        var flushTask = sut.FlushAsync(Book, TestContext.Current.CancellationToken);
        await exportStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        flushTask.IsCompleted.Should().BeFalse();
        delay.Requests[0].Completion.IsCanceled.Should().BeTrue();
        finishExport.TrySetResult();
        await flushTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FlushAsync_PropagatesRequestedCancellationWithoutStartingExport()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
        var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

        var act = () => sut.FlushAsync(Book, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sut.Cancel();
    }

    [Fact]
    public async Task Cancel_CancelsDelayAndPreventsAllFutureWork()
    {
        var delay = new ControlledDelay();
        var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
        var sut = CreateCoordinator(
            sync.Object,
            EnabledSettings(),
            credentials: true,
            delay.DelayAsync);

        sut.ScheduleExport(Book);
        sut.Cancel();
        sut.ScheduleExport(Book);
        await sut.FlushAsync(Book, TestContext.Current.CancellationToken);

        delay.Requests.Should().ContainSingle();
        var act = async () => await delay.Requests[0].Completion;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void AppRegistration_RegistersCoordinatorAsTransient()
    {
        var appPath = Path.Combine(ProjectRoot, "App.xaml.cs");

        File.ReadAllText(appPath)
            .Should().Contain("AddTransient<IReaderAutoSyncCoordinator, ReaderAutoSyncCoordinator>()");
    }

    public static TheoryData<Exception> ContainedOpenFailures => new()
    {
        new HttpRequestException("network unavailable"),
        new InvalidOperationException("oauth unavailable"),
    };

    private static string ProjectRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        "..",
        "Hoshi"));

    private static AppSettings EnabledSettings() => new()
    {
        TtuSyncSettings = new TtuSyncSettings
        {
            EnableSync = true,
            EnableAutoSync = true,
            UploadBooks = true,
        },
        StatisticsSettings = new NovelStatisticsSettings
        {
            EnableSync = true,
            SyncMode = StatisticsSyncMode.Replace,
        },
        SasayakiSettings = new SasayakiSettings
        {
            EnableSasayaki = true,
            EnableSync = true,
        },
    };

    private static ReaderAutoSyncCoordinator CreateCoordinator(
        ITtuSyncService sync,
        AppSettings current,
        bool credentials,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(current);
        var auth = new Mock<IGoogleDriveAuthService>();
        auth.SetupGet(service => service.HasCredentials).Returns(credentials);
        return new ReaderAutoSyncCoordinator(
            sync,
            settings.Object,
            auth.Object,
            Mock.Of<ILogger<ReaderAutoSyncCoordinator>>(),
            delay ?? ((duration, ct) => Task.Delay(duration, ct)));
    }

    private sealed class ControlledDelay
    {
        public List<DelayRequest> Requests { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = release.Task.WaitAsync(ct);
            Requests.Add(new DelayRequest(delay, release, completion));
            return completion;
        }
    }

    private sealed record DelayRequest(
        TimeSpan Delay,
        TaskCompletionSource ReleaseSource,
        Task Completion)
    {
        public void Release() => ReleaseSource.TrySetResult();
    }
}
