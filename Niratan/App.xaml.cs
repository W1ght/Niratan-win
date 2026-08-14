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
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Models.Novel;
using Niratan.Enums;
using Niratan.Services;
using Niratan.Services.Anki;
using Niratan.Services.Audio;
using Niratan.Services.Backup;
using Niratan.Services.Dictionary;
using Niratan.Services.GameControllers;
using Niratan.Services.Logging;
using Niratan.Services.Manga;
using Niratan.Services.Nyaa;
using Niratan.Services.Novels;
using Niratan.Services.Profiles;
using Niratan.Services.Sasayaki;
using Niratan.Services.Settings;
using Niratan.Services.Shortcuts;
using Niratan.Services.Storage;
using Niratan.Services.Sync;
using Niratan.Services.UI;
using Niratan.Services.Updates;
using Niratan.Services.Video;
using Niratan.Services.ZLibrary;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;
using Niratan.ViewModels.Dialogs;
using Niratan.ViewModels.Windowing;

namespace Niratan;

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
                path: Path.Combine(logsDir, "niratan-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                encoding: Encoding.UTF8,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();

        Log.Information("Niratan starting — version {Version}", AppInfoHelper.Version);

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

        // --- Step 7: First-chance exception logging (Debug only, Niratan code only) ---
#if DEBUG
        AppDomain.CurrentDomain.FirstChanceException += (_, args) =>
        {
            try
            {
                if (args.Exception.Source?.StartsWith("Niratan", StringComparison.OrdinalIgnoreCase) == true)
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
        services.AddTransient<BackupSettingsPageViewModel>();
        services.AddTransient<AboutSettingsPageViewModel>();
        services.AddTransient<DictionarySettingsPageViewModel>();
        services.AddTransient<ProfilesSettingsPageViewModel>();
        services.AddTransient<AudioSettingsPageViewModel>();
        services.AddTransient<VideoSettingsPageViewModel>();
        services.AddTransient<KeyboardShortcutsSettingsPageViewModel>();
        services.AddTransient<GameControllerSettingsPageViewModel>();
        services.AddTransient<SasayakiSettingsPageViewModel>();
        services.AddTransient<StatisticsSettingsPageViewModel>();
        services.AddTransient<NovelStatisticsDashboardViewModel>();
        services.AddTransient<TtuSyncSettingsPageViewModel>();
        services.AddTransient<AnkiSettingsPageViewModel>();
        services.AddTransient<NovelLibraryPageViewModel>();
        services.AddTransient<MangaLibraryPageViewModel>();
        services.AddTransient<MangaReaderViewModel>();
        services.AddTransient<NovelShelfManagementViewModel>();
        services.AddTransient<NyaaImportDialogViewModel>();
        services.AddTransient<ZLibraryDialogViewModel>();
        services.AddTransient<SasayakiResourcesViewModel>();
        services.AddTransient<VideoLibraryPageViewModel>();
        services.AddTransient<NovelLookupPageViewModel>();
        services.AddTransient<ReaderNavigationTransactionCoordinator>();
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
        services.AddSingleton<IReaderFontService, ReaderFontService>();
        services.AddSingleton<IProfileService, ProfileService>();
        services.AddSingleton<ProfileSettingsStore>();
        services.AddSingleton<ProfileRuntimeService>();
        services.AddSingleton<IProfileRuntimeService>(provider =>
            provider.GetRequiredService<ProfileRuntimeService>());
        services.AddSingleton<IDictionaryProfileContext>(provider =>
            provider.GetRequiredService<ProfileRuntimeService>());
        services.AddSingleton<IShortcutService, ShortcutService>();
        services.AddSingleton<IGameControllerService, GameControllerService>();
        services.AddSingleton<INiratanJsonFileStore, NiratanJsonFileStore>();
        services.AddSingleton<IVideoCatalogRepository, SQLiteVideoCatalogRepository>();
        services.AddSingleton<IVideoPlaybackHistoryStore, VideoPlaybackHistoryStore>();
        services.AddSingleton<IVideoFileNameParser, VideoFileNameParser>();
        services.AddSingleton<LocalVideoMetadataProvider>();
        services.AddSingleton<ILocalVideoMetadataProvider>(provider =>
            provider.GetRequiredService<LocalVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataProvider>(provider =>
            provider.GetRequiredService<LocalVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataMatcher, VideoMetadataMatcher>();
        services.AddSingleton<IVideoMetadataCoordinator, VideoMetadataCoordinator>();
        services.AddSingleton<IVideoLibraryScanCoordinator, VideoLibraryScanCoordinator>();
        services.AddSingleton<IVideoMetadataTransport, VideoMetadataTransport>();
        services.AddSingleton<IVideoMetadataCredentialStore, WindowsCredentialVideoMetadataStore>();
        services.AddSingleton<IVideoArtworkCache, VideoArtworkCache>();
        services.AddSingleton<TmdbVideoMetadataProvider>();
        services.AddSingleton<TvMazeVideoMetadataProvider>();
        services.AddSingleton<AniListVideoMetadataProvider>();
        services.AddSingleton<AniDbTitleIndexProvider>();
        services.AddSingleton<BangumiVideoMetadataProvider>();
        services.AddSingleton<TvDbLicenseGatedProvider>();
        services.AddSingleton<IVideoMetadataProvider>(provider => provider.GetRequiredService<TmdbVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataProvider>(provider => provider.GetRequiredService<TvMazeVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataProvider>(provider => provider.GetRequiredService<AniListVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataProvider>(provider => provider.GetRequiredService<AniDbTitleIndexProvider>());
        services.AddSingleton<IVideoMetadataProvider>(provider => provider.GetRequiredService<BangumiVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataProvider>(provider => provider.GetRequiredService<TvDbLicenseGatedProvider>());
        services.AddSingleton<IVideoMetadataSearchProvider>(provider => provider.GetRequiredService<TmdbVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataSearchProvider>(provider => provider.GetRequiredService<TvMazeVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataSearchProvider>(provider => provider.GetRequiredService<AniListVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataSearchProvider>(provider => provider.GetRequiredService<AniDbTitleIndexProvider>());
        services.AddSingleton<IVideoMetadataSearchProvider>(provider => provider.GetRequiredService<BangumiVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataSearchProvider>(provider => provider.GetRequiredService<TvDbLicenseGatedProvider>());
        services.AddSingleton<IVideoMetadataDetailsProvider>(provider => provider.GetRequiredService<TmdbVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataDetailsProvider>(provider => provider.GetRequiredService<TvMazeVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataDetailsProvider>(provider => provider.GetRequiredService<AniListVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataDetailsProvider>(provider => provider.GetRequiredService<BangumiVideoMetadataProvider>());
        services.AddSingleton<IVideoMetadataDetailsProvider>(provider => provider.GetRequiredService<TvDbLicenseGatedProvider>());
        services.AddSingleton<IVideoArtworkProvider>(provider => provider.GetRequiredService<TmdbVideoMetadataProvider>());
        services.AddSingleton<IVideoArtworkProvider>(provider => provider.GetRequiredService<TvMazeVideoMetadataProvider>());
        services.AddSingleton<IVideoArtworkProvider>(provider => provider.GetRequiredService<AniListVideoMetadataProvider>());
        services.AddSingleton<IVideoArtworkProvider>(provider => provider.GetRequiredService<TvDbLicenseGatedProvider>());
        services.AddSingleton<IVideoDataService, VideoDataService>();
        services.AddSingleton<INovelBookStorageService, NovelBookStorageService>();
        services.AddSingleton<INovelShelfService, NovelShelfService>();
        services.AddSingleton<INovelStorageMigrationService, NovelStorageMigrationService>();
        services.AddSingleton<NovelStorageAccessState>();
        services.AddSingleton<INovelStorageAccessState>(provider =>
            provider.GetRequiredService<NovelStorageAccessState>());
        services.AddSingleton<IEpubParserService, EpubParserService>();
        services.AddSingleton<INovelEpubImportService, NovelEpubImportService>();
        services.AddSingleton<INovelLibraryService, NovelLibraryService>();
        services.AddSingleton<MangaSourceIndexer>();
        services.AddSingleton<IMangaCatalogStore>(_ =>
            new MangaCatalogStore(AppDataHelper.GetMangaCatalogPath()));
        services.AddSingleton<IMangaPageProvider, MangaPageProvider>();
        services.AddSingleton<IMangaTextRegionService, MangaTextRegionService>();
        services.AddSingleton<IMangaOcrService, MangaOcrService>();
        services.AddSingleton<ISuwayomiService, SuwayomiService>();
        services.AddSingleton<IMihonExtensionService, MihonExtensionService>();
        services.AddSingleton<MangaLibraryService>();
        services.AddSingleton<IMangaLibraryService>(provider =>
            provider.GetRequiredService<MangaLibraryService>());
        services.AddSingleton<IMangaReaderWindowService, MangaReaderWindowService>();
        services.AddSingleton<NyaaRssParser>();
        services.AddSingleton<INyaaClient, NyaaRssClient>();
        services.AddSingleton<ResourcePackageAnalyzer>();
        services.AddSingleton<IResourcePackageImportService, ResourcePackageImportService>();
        services.AddSingleton<ITorrentDownloadService, MonoTorrentDownloadService>();
        services.AddSingleton<INyaaDownloadManager, NyaaDownloadManager>();
        services.AddSingleton<IZLibraryClient, ZLibraryClient>();
        services.AddSingleton<IZLibraryCredentialStore, WindowsCredentialZLibraryCredentialStore>();
        services.AddSingleton<IZLibraryService, ZLibraryService>();
        services.AddSingleton<IVideoLibraryService, VideoLibraryService>();
        services.AddSingleton<IRemoteVideoResolver, YoutubeExplodeRemoteVideoResolver>();
        services.AddSingleton<IAnime4KShaderService, Anime4KShaderService>();
        services.AddSingleton<IVideoMiningHistoryStore>(provider =>
            new VideoMiningHistoryStore(
                provider.GetRequiredService<ISettingsService>().Current.VideoSettings.MiningHistoryLimit));
        services.AddTransient<IVideoPlaybackEngine, MpvPlaybackEngine>();
        services.AddSingleton<IVideoMiningMediaExtractor, LibMpvVideoMiningMediaExtractor>();
        services.AddSingleton<IVideoThumbnailService, VideoThumbnailService>();
        services.AddSingleton<IVideoSubtitleTranscriptExtractor, FfmpegVideoSubtitleTranscriptExtractor>();
        services.AddSingleton<IVideoPlayerWindowService, VideoPlayerWindowService>();
        services.AddSingleton<IVideoSameFolderPlaylistResolver, VideoSameFolderPlaylistResolver>();
        services.AddSingleton<SubtitleParserService>();
        services.AddSingleton<INovelBookSidecarService, NovelBookSidecarService>();
        services.AddSingleton<IReaderImageGalleryService, ReaderImageGalleryService>();
        services.AddSingleton<INovelStatisticsSidecarService, NovelStatisticsSidecarService>();
        services.AddSingleton<INovelStatisticsMutationCoordinator, NovelStatisticsMutationCoordinator>();
        services.AddTransient<IReaderStatisticsSession>(provider =>
            new ReaderStatisticsSession(
                provider.GetRequiredService<INovelStatisticsSidecarService>(),
                TimeProvider.System,
                () => provider.GetRequiredService<ISettingsService>()
                    .Current.StatisticsSettings.ResetTimeMinutes));
        services.AddSingleton<NovelStatisticsDashboardCache>();
        services.AddSingleton<INovelStatisticsDashboardService>(provider =>
            new NovelStatisticsDashboardService(
                provider.GetRequiredService<INovelStatisticsSidecarService>(),
                provider.GetRequiredService<INovelBookSidecarService>(),
                provider.GetRequiredService<NovelStatisticsDashboardCache>(),
                () => provider.GetRequiredService<ISettingsService>()
                    .Current.StatisticsSettings.ResetTimeMinutes));
        services.AddSingleton<IReaderHighlightService, ReaderHighlightService>();
        services.AddSingleton<ISasayakiSidecarService, SasayakiSidecarService>();
        services.AddSingleton<ISasayakiMatchService, SasayakiMatchService>();
        services.AddSingleton(new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30),
        });
        services.AddSingleton<IAppUpdateService, GitHubAppUpdateService>();
        services.AddSingleton<IAppUpdateInstallerLauncher, SystemAppUpdateInstallerLauncher>();
        services.AddSingleton<GoogleDriveTokenClient>();
        services.AddSingleton<IGoogleDriveCredentialStore, WindowsCredentialGoogleDriveCredentialStore>();
        services.AddSingleton<IGoogleOAuthLoopbackReceiver, GoogleOAuthLoopbackReceiver>();
        services.AddSingleton<IBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton<IGoogleDriveAuthService, GoogleDriveAuthService>();
        services.AddSingleton<IGoogleDriveSyncCache, GoogleDriveSyncCache>();
        services.AddSingleton<IGoogleDriveCoverCacheService, GoogleDriveCoverCacheService>();
        services.AddSingleton<ITtuBookDataConverter, TtuBookDataConverter>();
        services.AddSingleton<ITtuBackupBookDataConverter, TtuBookDataConverter>();
        services.AddSingleton<ITtuBookImportService, TtuBookImportService>();
        services.AddSingleton<ITtuSyncService, TtuSyncService>();
        RegisterReaderAutoSyncCoordinator(services);
        services.AddSingleton<ITtuSyncRemoteStore, GoogleDriveTtuSyncRemoteStore>();
        services.AddSingleton<DictionaryLookupService>();
        services.AddSingleton<IDictionaryLookupService>(provider =>
            provider.GetRequiredService<DictionaryLookupService>());
        services.AddSingleton<IDictionaryPopupRequestService, DictionaryPopupRequestService>();
        services.AddSingleton<IDictionaryImportService, DictionaryImportService>();
        services.AddSingleton<IDictionaryCatalogService, DictionaryCatalogService>();
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
        services.AddSingleton<IBackupService, BackupService>();
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

            var unifiedTheme = readerSettings.Current.ResolveUnifiedTheme(settings.Current.Theme);
            if (readerSettings.Current.Theme != unifiedTheme)
                readerSettings.Set(s => s.Theme, (ReaderTheme?)unifiedTheme);
            if (unifiedTheme == ReaderTheme.Custom
                && readerSettings.Current.CustomInterfaceTheme is null)
            {
                readerSettings.Set(
                    s => s.CustomInterfaceTheme,
                    (ThemeMode?)settings.Current.Theme);
            }

            var interfaceTheme = readerSettings.Current.ResolveInterfaceTheme(settings.Current.Theme);
            if (settings.Current.Theme != interfaceTheme)
                settings.Set(s => s.Theme, interfaceTheme);

            MainWindow = new MainWindow();
            MainWindow.Activate();
            MainWindow.SetMicaBackdrop();
            GetService<IGameControllerService>().Start();

            StartHangWatchdog();

            var novelMigrationResult = await GetService<INovelStorageMigrationService>()
                .MigrateAsync();
            GetService<NovelStorageAccessState>().Apply(novelMigrationResult);
            if (novelMigrationResult.IsReadOnly)
            {
                GetService<INotificationService>().ShowError(
                    novelMigrationResult.ErrorMessage
                        ?? "Novel storage migration requires recovery.",
                    "Novel library is read-only");
            }

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

    internal static void RegisterReaderAutoSyncCoordinator(IServiceCollection services) =>
        services.AddTransient<IReaderAutoSyncCoordinator, ReaderAutoSyncCoordinator>();

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

        var playlist = GetService<IVideoSameFolderPlaylistResolver>().Resolve(options.VideoPath);
        await GetService<IVideoPlayerWindowService>().OpenAsync(video, playlist.Count > 0 ? playlist : [video]);
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
