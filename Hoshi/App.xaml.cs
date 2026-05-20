using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Serilog;
using Hoshi.Helpers;
using Hoshi.Services;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Logging;
using Hoshi.Services.Novels;
using Hoshi.Services.Settings;
using Hoshi.Services.Storage;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;

namespace Hoshi;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    private readonly IServiceProvider _services;

    public App()
    {
        InitializeComponent();
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "en-US";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(AppDataHelper.GetAppDataPath(), "Logs", "hoshi-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                encoding: Encoding.UTF8,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] ({SourceContext}) {Message:lj}{NewLine}{Exception}"
            )
            .CreateLogger();

        Log.Information("Hoshi starting — version {Version}", AppInfoHelper.Version);

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog(dispose: true));

        services.AddTransient<ShellPageViewModel>();
        services.AddTransient<NavigationPageViewModel>();
        services.AddTransient<SettingsPageViewModel>();
        services.AddTransient<DictionarySettingsPageViewModel>();
        services.AddTransient<NovelLibraryPageViewModel>();
        services.AddTransient<NovelReaderPageViewModel>();
        services.AddTransient<ViewModels.Pages.LogsPageViewModel>();
        services.AddTransient<ViewModels.Dialogs.ReaderAppearanceViewModel>();

        services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IReaderSettingsService, ReaderSettingsService>();
        services.AddSingleton<IDataService, DataService>();
        services.AddSingleton<IEpubParserService, EpubParserService>();
        services.AddSingleton<INovelEpubImportService, NovelEpubImportService>();
        services.AddSingleton<INovelLibraryService, NovelLibraryService>();
        services.AddSingleton<IDictionaryLookupService, DictionaryLookupService>();
        services.AddSingleton<IDictionaryImportService, DictionaryImportService>();
        services.AddSingleton<ILogReaderService, LogReaderService>();
        _services = services.BuildServiceProvider();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var settings = GetService<ISettingsService>();
        await settings.LoadAsync();

        var readerSettings = GetService<IReaderSettingsService>();
        await readerSettings.LoadAsync();

        MainWindow = new MainWindow();
        MainWindow.Activate();

        try
        {
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
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Failed to initialize Hoshi — navigating to InitializationErrorPage");
            MainWindow.NavigateToError(ex);
        }
        finally
        {
            MainWindow.SetMicaBackdrop();
        }
    }

    public static T GetService<T>()
        where T : class => ((App)Current)._services.GetRequiredService<T>();

    private async Task InitializeAppAsync()
    {
        await Task.Delay(400);
    }
}
