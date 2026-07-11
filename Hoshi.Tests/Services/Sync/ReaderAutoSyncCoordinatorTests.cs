using System.Text.Json;
using FluentAssertions;
using Hoshi.Models;
using Hoshi.Models.Sasayaki;
using Hoshi.Models.Settings;
using Hoshi.Models.Sync;
using Hoshi.Services.Settings;
using Hoshi.Services.Sync;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task ImportOnOpenAsync_ContainsNonRequestedSyncFailures(Exception failure)
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
    public async Task ImportOnOpenAsync_ContainsCredentialGateFailure()
    {
        var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(EnabledSettings());
        var auth = new Mock<IGoogleDriveAuthService>();
        auth.SetupGet(service => service.HasCredentials)
            .Throws(new CredentialStoreException("credential payload must not escape"));
        var sut = new ReaderAutoSyncCoordinator(
            sync.Object,
            settings.Object,
            auth.Object,
            Mock.Of<ILogger<ReaderAutoSyncCoordinator>>());

        (await sut.ImportOnOpenAsync(Book, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [Fact]
    public async Task ImportOnOpenAsync_SkipsPermanentlyAfterCancel()
    {
        var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
        var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

        sut.Cancel();

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
        var secondExportFinished = new TaskCompletionSource(
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
                    else
                    {
                        secondExportFinished.TrySetResult();
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
        firstExportFinished.TrySetResult();
        await secondExportFinished.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sut.Cancel();

        exportOptions.Should().HaveCount(2);
        exportOptions.Should().OnlyContain(option =>
            option.Direction == TtuSyncDirection.ExportToTtu && !option.ImportOnly);
        maximumActiveExports.Should().Be(1);
    }

    [Fact]
    public async Task ScheduleExport_DropsPendingWorkWhenPrerequisiteDisappearsBeforeDelay()
    {
        var delay = new ControlledDelay();
        var debounceDrained = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = EnabledSettings();
        var sync = new Mock<ITtuSyncService>(MockBehavior.Strict);
        var sut = CreateCoordinator(
            sync.Object,
            settings,
            credentials: true,
            delay.DelayAsync,
            () =>
            {
                debounceDrained.TrySetResult();
                return Task.CompletedTask;
            });

        sut.ScheduleExport(Book);
        settings.TtuSyncSettings.EnableAutoSync = false;
        delay.Requests[0].Release();
        await debounceDrained.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await sut.FlushAsync(Book, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ScheduleExport_PreservesNewerPendingChangeWhenOlderRuntimeGateFails()
    {
        var delay = new ControlledDelay();
        var blockedGateEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedGate = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exportCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var credentialRead = 0;
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(EnabledSettings());
        var auth = new Mock<IGoogleDriveAuthService>();
        auth.SetupGet(service => service.HasCredentials).Returns(() =>
        {
            if (Interlocked.Increment(ref credentialRead) != 2)
                return true;

            blockedGateEntered.TrySetResult();
            releaseBlockedGate.Task.GetAwaiter().GetResult();
            return false;
        });
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                exportCompleted.TrySetResult();
                return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
            });
        var sut = new ReaderAutoSyncCoordinator(
            sync.Object,
            settings.Object,
            auth.Object,
            Mock.Of<ILogger<ReaderAutoSyncCoordinator>>(),
            delay.DelayAsync);

        sut.ScheduleExport(Book);
        delay.Requests[0].Release();
        await blockedGateEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        sut.ScheduleExport(Book);
        releaseBlockedGate.TrySetResult();

        var retryDelay = await delay.WaitForRequestAsync(
            index: 1,
            TestContext.Current.CancellationToken);
        retryDelay.Release();
        await exportCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sut.Cancel();
    }

    [Fact]
    public async Task ScheduleExport_RestartsDelayWhenChangeArrivesAfterEmptyCheckBeforeCleanup()
    {
        var delay = new ControlledDelay();
        var cleanupWindowEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondExportCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var exportCount = 0;
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                if (Interlocked.Increment(ref exportCount) == 2)
                    secondExportCompleted.TrySetResult();
                return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
            });
        var settings = new Mock<ISettingsService>();
        settings.SetupGet(service => service.Current).Returns(EnabledSettings());
        var auth = new Mock<IGoogleDriveAuthService>();
        auth.SetupGet(service => service.HasCredentials).Returns(true);
        var sut = new ReaderAutoSyncCoordinator(
            sync.Object,
            settings.Object,
            auth.Object,
            Mock.Of<ILogger<ReaderAutoSyncCoordinator>>(),
            delay.DelayAsync,
            async () =>
            {
                cleanupWindowEntered.TrySetResult();
                await releaseCleanup.Task;
            });

        sut.ScheduleExport(Book);
        delay.Requests[0].Release();
        await cleanupWindowEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        sut.ScheduleExport(Book);
        releaseCleanup.TrySetResult();

        var restartedDelay = await delay.WaitForRequestAsync(
            index: 1,
            TestContext.Current.CancellationToken);
        restartedDelay.Release();
        await secondExportCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sut.Cancel();

        exportCount.Should().Be(2);
    }

    [Fact]
    public async Task ScheduleExport_ContainsFailureAndRunsPendingFollowUp()
    {
        var delay = new ControlledDelay();
        var firstExportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var failFirstExport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpCompleted = new TaskCompletionSource(
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

                followUpCompleted.TrySetResult();
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

        failFirstExport.TrySetResult();
        await followUpCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sut.Cancel();

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
    public async Task FlushAsync_ConcurrentCallersJoinOneExport()
    {
        var exportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var finishExport = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref callCount);
                exportStarted.TrySetResult();
                await finishExport.Task;
                return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
            });
        var sut = CreateCoordinator(sync.Object, EnabledSettings(), credentials: true);

        var firstFlush = sut.FlushAsync(Book, TestContext.Current.CancellationToken);
        await exportStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        var secondFlush = sut.FlushAsync(Book, TestContext.Current.CancellationToken);

        callCount.Should().Be(1);
        secondFlush.IsCompleted.Should().BeFalse();
        finishExport.TrySetResult();
        await Task.WhenAll(firstFlush, secondFlush).WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        callCount.Should().Be(1);
    }

    [Fact]
    public async Task FlushAsync_RestoresConsumedChangeWhenActiveExportIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var delay = new ControlledDelay();
        var firstExportStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var retryCompleted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callCount = 0;
        var sync = new Mock<ITtuSyncService>();
        sync.Setup(service => service.SyncBookAsync(
                Book,
                It.IsAny<TtuSyncOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (NovelBook _, TtuSyncOptions _, CancellationToken ct) =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    firstExportStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }

                retryCompleted.TrySetResult();
                return new TtuSyncResult(TtuSyncResultKind.Exported, Book.Title);
            });
        var sut = CreateCoordinator(
            sync.Object,
            EnabledSettings(),
            credentials: true,
            delay.DelayAsync);

        var cancelledFlush = sut.FlushAsync(Book, cts.Token);
        await firstExportStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        cts.Cancel();

        var cancelledAct = async () => await cancelledFlush;
        await cancelledAct.Should().ThrowAsync<OperationCanceledException>();
        var retryDelay = await delay.WaitForRequestAsync(
            index: 0,
            TestContext.Current.CancellationToken);
        retryDelay.Release();
        await retryCompleted.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        sut.Cancel();

        callCount.Should().Be(2);
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
    public void DependencyInjection_ResolvesDistinctTransientCoordinators()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Mock.Of<ITtuSyncService>());
        services.AddSingleton(Mock.Of<ISettingsService>());
        services.AddSingleton(Mock.Of<IGoogleDriveAuthService>());
        services.AddSingleton(Mock.Of<ILogger<ReaderAutoSyncCoordinator>>());
        services.AddTransient<IReaderAutoSyncCoordinator, ReaderAutoSyncCoordinator>();
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IReaderAutoSyncCoordinator>();
        var second = provider.GetRequiredService<IReaderAutoSyncCoordinator>();

        first.Should().BeOfType<ReaderAutoSyncCoordinator>();
        second.Should().BeOfType<ReaderAutoSyncCoordinator>();
        second.Should().NotBeSameAs(first);
    }

    public static TheoryData<Exception> ContainedOpenFailures => new()
    {
        new HttpRequestException("network unavailable"),
        new InvalidOperationException("oauth unavailable"),
        new JsonException("remote json unavailable"),
        new IOException("credential storage unavailable"),
    };

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
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<Task>? onDebounceDrained = null)
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
            delay ?? ((duration, ct) => Task.Delay(duration, ct)),
            onDebounceDrained ?? (static () => Task.CompletedTask));
    }

    private sealed class ControlledDelay
    {
        private readonly object _gate = new();
        private readonly List<DelayRequest> _requests = [];
        private TaskCompletionSource _requestAdded = NewSignal();

        public IReadOnlyList<DelayRequest> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public Task DelayAsync(TimeSpan delay, CancellationToken ct)
        {
            var release = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completion = release.Task.WaitAsync(ct);
            lock (_gate)
            {
                _requests.Add(new DelayRequest(delay, release, completion));
                _requestAdded.TrySetResult();
                _requestAdded = NewSignal();
            }

            return completion;
        }

        public async Task<DelayRequest> WaitForRequestAsync(
            int index,
            CancellationToken ct)
        {
            while (true)
            {
                Task requestAdded;
                lock (_gate)
                {
                    if (_requests.Count > index)
                        return _requests[index];
                    requestAdded = _requestAdded.Task;
                }

                await requestAdded.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        }

        private static TaskCompletionSource NewSignal() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record DelayRequest(
        TimeSpan Delay,
        TaskCompletionSource ReleaseSource,
        Task Completion)
    {
        public void Release() => ReleaseSource.TrySetResult();
    }

    private sealed class CredentialStoreException(string message) : Exception(message);
}
