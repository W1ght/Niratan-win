using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hoshi.Models;
using Hoshi.Services.Logging;
using Serilog;

namespace Hoshi.ViewModels.Pages;

public partial class LogsPageViewModel : ObservableObject
{
    public ObservableCollection<LogEntry> RecentLogs { get; } = [];
    public ObservableCollection<LogEntry> ErrorLogs { get; } = [];

    [ObservableProperty]
    public partial string LogStatusText { get; set; } = "Loading...";

    public IAsyncRelayCommand RefreshLogsCommand { get; }

    public LogsPageViewModel()
    {
        RefreshLogsCommand = new AsyncRelayCommand(RefreshLogsAsync);
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            await RefreshLogsAsync();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LogsPage] Failed to load logs");
        }
    }

    private async Task RefreshLogsAsync()
    {
        var logReader = App.GetService<ILogReaderService>();
        var logs = await logReader.ReadRecentLogsAsync(500);
        var errors = await logReader.ReadErrorLogsAsync(200);

        var dispatcher = App.MainWindow?.DispatcherQueue;
        if (dispatcher == null) return;

        dispatcher.TryEnqueue(() =>
        {
            try
            {
                RecentLogs.Clear();
                foreach (var log in logs)
                    RecentLogs.Add(log);

                ErrorLogs.Clear();
                foreach (var err in errors)
                    ErrorLogs.Add(err);

                LogStatusText = $"{logs.Count} entries (refreshed at {DateTime.Now:HH:mm:ss})";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[LogsPage] Failed to update log viewer UI");
            }
        });
    }
}
