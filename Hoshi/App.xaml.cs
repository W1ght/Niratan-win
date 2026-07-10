using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Serilog;
using Hoshi.Helpers;
using Hoshi.Models;
using Hoshi.Models.Novel;
using Hoshi.Services;
using Hoshi.Services.Anki;
using Hoshi.Services.Audio;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Logging;
using Hoshi.Services.Novels;
using Hoshi.Services.Profiles;
using Hoshi.Services.Sasayaki;
using Hoshi.Services.Settings;
using Hoshi.Services.Shortcuts;
using Hoshi.Services.Storage;
using Hoshi.Services.Sync;
using Hoshi.Services.UI;
using Hoshi.Services.Video;
using Hoshi.ViewModels.Pages;
using Hoshi.ViewModels.Windowing;

namespace Hoshi;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    private readonly IServiceProvider _services;
    private static DispatcherQueueTimer? s_hangWatchdogTimer;

    public App()
    {
        // --- Step 1: Ensure Logs directory exists BEFORE configuring Serilog ---
        var logsDir = Path.Combine(AppDataHelper.GetAppDataPath(), "Logs");
        Directory.CreateDirectory(logsDir);

        // --- Step 2: Configure Serilog before anything else ---
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logsDir, "hoshi-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                encoding: Encoding.UTF8,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();

        Log.Information("Hoshi starting — version {Version}", AppInfoHelper.Version);

        // --- Step 3: WinUI-level unhandled exceptions (UI thread, XAML, compositor) ---
        this.UnhandledException += (_, args) =>
        {
            try
            {
                Log.Fatal(args.Exception, "[Crash] WinUI UnhandledException — {Message}", args.Message);
            }
            catch { /* not much we can do */ }
            finally
            {
                Log.CloseAndFlush();
            }
        };

        // --- Step 4: CLR-level unhandled exceptions on background threads ---
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            try
            {
                Log.Fatal(ex ?? new Exception(args.ExceptionObject?.ToString()),
                    "[Crash] CLR UnhandledException — app is about to terminate");
            }
            catch { /* not much we can do */ }
            finally
            {
                Log.CloseAndFlush();
            }
        };

        // --- Step 5: Fire-and-forget tasks that throw and are never awaited ---
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            try
            {
                Log.Error(args.Exception.GetBaseException(),
                    "[Crash] UnobservedTaskException — fire-and-forget task threw");
            }
            catch { /* not much we can do */ }
            args.SetObserved();
        };

        // --- Step 6: Flush logs on normal process exit ---
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Log.CloseAndFlush(); }
            catch { /* not much we can do */ }
        };

        // --- Step 7: First-chance exception logging (Debug only, Hoshi code only) ---
#if DEBUG
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            try
            {
                if (args.Exception.Source?.StartsWith("Hoshi", StringComparison.OrdinalIgnoreCase) == true)
                    Log.Debug(args.Exception, "[FirstChance]");
            }
            catch { /* not much we can do */ }
        };
#endif

        // --- Step 8: Initialize XAML (after logging is ready) ---
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        services.AddTransient<ShellPageViewModel>();
        services.AddTransient<NavigationPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<DictionarySettingsPageViewModel>();
        services.AddTransient<ProfilesSettingsPageViewModel>();
        services.AddTransient<AudioSettingsPageViewModel>();
        services.AddTransient<VideoSettingsPageViewModel>();
        services.AddTransient<KeyboardShortcutsSettingsPageViewModel>();
        services.AddTransient<SasayakiSettingsPageViewModel>();
        services.AddTransient<StatisticsSettingsPageViewModel>();
        services.AddTransient<TtuSyncSettingsPageViewModel>();
        services.AddTransient<AnkiSettingsPageViewModel>();
        services.AddTransient<NovelLibraryPageViewModel>();
        services.AddTransient<VideoLibraryPageViewModel>();
        services.AddTransient<NovelLookupPageViewModel>();
        services.AddTransient<NovelReaderPageViewModel>();
        services.AddTransient<VideoPlayerViewModel>();
        services.AddTransient<GlobalLookupWindowViewModel>();
        services.AddTransient<ViewModels.Pages.LogsPageViewModel>();

        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IFileRevealService, FileRevealService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IReaderSettingsService, ReaderSettingsService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<ProfileSettingsStore>();
        services.AddSingleton<ProfileRuntimeService>();
        services.AddSingleton<IProfileRuntimeService>(provider =>
            provider.GetRequiredService<ProfileRuntimeService>());
        services.AddSingleton<IDictionaryProfileContext>(provider =>
            provider.GetRequiredService<ProfileRuntimeService>());
        services.AddSingleton<IShortcutService, ShortcutService>();
        services.AddSingleton<IDataService, DataService>();
        services.AddSingleton<INiratanJsonFileStore, NiratanJsonFileStore>();
        services.AddSingleton<INovelBookStorageService, NovelBookStorageService>();
        services.AddSingleton<NovelStorageAccessState>();
        services.AddSingleton<INovelStorageAccessState>(provider =>
            provider.GetRequiredService<NovelStorageAccessState>());
        services.AddSingleton<IEpubParserService, EpubParserService>();
        services.AddSingleton<INovelEpubImportService, NovelEpubImportService>();
        services.AddSingleton<INovelLibraryService, NovelLibraryService>();
        services.AddSingleton<IVideoLibraryService, VideoLibraryService>();
        services.AddSingleton<IVideoMiningHistoryStore>(provider =>
            new VideoMiningHistoryStore(
                provider.GetRequiredService<ISettingsService>().Current.VideoSettings.MiningHistoryLimit));
        services.AddTransient<IVideoPlaybackEngine, MpvPlaybackEngine>();
        services.AddSingleton<IVideoMiningMediaExtractor, LibMpvVideoMiningMediaExtractor>();
        services.AddSingleton<IVideoThumbnailService, VideoThumbnailService>();
        services.AddSingleton<IVideoSubtitleTranscriptExtractor, FfmpegVideoSubtitleTranscriptExtractor>();
        services.AddSingleton<IVideoPlayerWindowService, VideoPlayerWindowService>();
        services.AddSingleton<SubtitleParserService>();
        services.AddSingleton<INovelBookSidecarService, NovelBookSidecarService>();
        services.AddSingleton<INovelStatisticsSidecarService, NovelStatisticsSidecarService>();
        services.AddSingleton<INovelStatisticsDashboardService, NovelStatisticsDashboardService>();
        services.AddSingleton<IReaderHighlightService, ReaderHighlightService>();
        services.AddSingleton<ISasayakiSidecarService, SasayakiSidecarService>();
        services.AddSingleton<ISasayakiMatchService, SasayakiMatchService>();
        services.AddSingleton(new HttpClient());
        services.AddSingleton<GoogleDriveTokenClient>();
        services.AddSingleton<IGoogleDriveCredentialStore, WindowsCredentialGoogleDriveCredentialStore>();
        services.AddSingleton<IGoogleOAuthLoopbackReceiver, GoogleOAuthLoopbackReceiver>();
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IGoogleDriveAuthService, GoogleDriveAuthService>();
        services.AddSingleton<IGoogleDriveSyncCache, GoogleDriveSyncCache>();
        services.AddSingleton<ITtuBookDataConverter, TtuBookDataConverter>();
        services.AddSingleton<ITtuBookImportService, TtuBookImportService>();
        services.AddSingleton<ITtuSyncService, TtuSyncService>();
        services.AddSingleton<ITtuSyncRemoteStore, GoogleDriveTtuSyncRemoteStore>();
        services.AddSingleton<IDictionaryLookupService, DictionaryLookupService>();
        services.AddSingleton<IDictionaryPopupRequestService, DictionaryPopupRequestService>();
        services.AddSingleton<IDictionaryImportService, DictionaryImportService>();
        services.AddSingleton<IGlobalLookupWindowService, GlobalLookupWindowService>();
        services.AddSingleton<IGlobalLookupPopupService, GlobalLookupPopupService>();
        services.AddSingleton<IGlobalSelectionLookupService, GlobalSelectionLookupService>();
        services.AddSingleton<IGlobalLookupHotKeyRegistrar, Win32GlobalLookupHotKeyRegistrar>();
        services.AddSingleton<UIAutomationSelectedTextReader>();
        services.AddSingleton<Win32FocusedEditSelectedTextReader>();
        services.AddSingleton<ISelectedTextReader>(provider => new CascadingSelectedTextReader(
            provider.GetRequiredService<UIAutomationSelectedTextReader>(),
            provider.GetRequiredService<Win32FocusedEditSelectedTextReader>()));
        services.AddSingleton<ILogReaderService, LogReaderService>();
        services.AddSingleton<IAudioService, AudioService>();
        services.AddSingleton<IAnkiService, AnkiService>();
        _services = services.BuildServiceProvider();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var settings = GetService<ISettingsService>();
            await settings.LoadAsync();

            var readerSettings = GetService<IReaderSettingsService>();
            await readerSettings.LoadAsync();

            var profiles = GetService<IProfileService>();
            await profiles.LoadAsync();
            await GetService<IProfileRuntimeService>().InitializeAsync();

            MainWindow = new MainWindow();
            MainWindow.Activate();
            MainWindow.SetMicaBackdrop();

            StartHangWatchdog();

            var migrator = new DatabaseMigrator(
                GetService<ILogger<DatabaseMigrator>>(),
                $"Data Source={Path.Combine(AppDataHelper.GetDataPath(), "hoshi.db")}"
            );
            await migrator.MigrateAsync();
            Log.Information("Database ready");

            await InitializeAppAsync();

            _ = Task.Run(async () =>
            {
                try
                {
                    await GetService<IDictionaryLookupService>().RebuildQueryAsync();
                    Log.Information("Dictionary lookup index prewarmed");
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Dictionary lookup index prewarm failed");
                }
            });

            MainWindow.NavigateToShell();
            await GetService<IGlobalSelectionLookupService>().InitializeAsync();
            await OpenVideoFromLaunchArgumentsAsync(args.Arguments);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[Crash] OnLaunched failed — navigating to error page");
            try
            {
                if (MainWindow != null)
                    MainWindow.NavigateToError(ex);
            }
            catch (Exception navEx)
            {
                Log.Fatal(navEx, "[Crash] Even the error page navigation failed");
            }
        }
    }

    public static T GetService<T>()
        where T : class => ((App)Current)._services.GetRequiredService<T>();

    private async Task OpenVideoFromLaunchArgumentsAsync(string? arguments)
    {
        var options = VideoLaunchOptionsParser.Parse(arguments)
            ?? VideoLaunchOptionsParser.Parse(Environment.GetCommandLineArgs().Skip(1));
        if (options == null)
            return;

        if (!File.Exists(options.VideoPath))
        {
            Log.Warning("[Video] Launch video path does not exist: {Path}", options.VideoPath);
            return;
        }

        var subtitlePath = !string.IsNullOrWhiteSpace(options.SubtitlePath) && File.Exists(options.SubtitlePath)
            ? options.SubtitlePath
            : VideoLibraryService.FindSidecarSubtitle(options.VideoPath);

        var video = new VideoItem
        {
            Title = Path.GetFileNameWithoutExtension(options.VideoPath),
            FilePath = options.VideoPath,
            SubtitlePath = subtitlePath,
            ImportedAt = DateTime.UtcNow,
        };

        await GetService<IVideoPlayerWindowService>().OpenAsync(video);
    }

    private async Task InitializeAppAsync()
    {
        await Task.Delay(400);
    }

    private static void StartHangWatchdog()
    {
        if (MainWindow?.DispatcherQueue == null) return;

        var watchdogState = new UiHangWatchdogState(Environment.TickCount64);

        s_hangWatchdogTimer = MainWindow.DispatcherQueue.CreateTimer();
        s_hangWatchdogTimer.Interval = TimeSpan.FromSeconds(1);
        s_hangWatchdogTimer.IsRepeating = true;
        s_hangWatchdogTimer.Tick += (_, _) => watchdogState.RecordUiTick(Environment.TickCount64);
        s_hangWatchdogTimer.Start();

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(3000);
                var now = Environment.TickCount64;
                if (watchdogState.ShouldReportHang(now, thresholdMs: 4000))
                {
                    try
                    {
                        Log.Warning(
                            "[Hang] UI thread unresponsive for {Seconds}s",
                            watchdogState.ElapsedSinceLastUiTickMs(now) / 1000);
                    }
                    catch { /* not much we can do */ }
                }
            }
        });

        Log.Information("[Watchdog] UI hang monitor started");
    }
}
