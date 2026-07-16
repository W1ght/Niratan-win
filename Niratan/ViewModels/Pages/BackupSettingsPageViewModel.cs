using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Services.Backup;
using Niratan.Services.UI;

namespace Niratan.ViewModels.Pages;

public partial class BackupSettingsPageViewModel : ObservableObject
{
    private readonly IBackupService _backupService;
    private readonly IDialogService _dialogService;
    private readonly INotificationService _notifications;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRunActions))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    public bool CanRunActions => !IsBusy;

    public BackupSettingsPageViewModel(
        IBackupService backupService,
        IDialogService dialogService,
        INotificationService notifications)
    {
        _backupService = backupService;
        _dialogService = dialogService;
        _notifications = notifications;
    }

    [RelayCommand]
    private Task BackupBooksAsync() => BackupHoshiAsync(HoshiBackupTarget.Books);

    [RelayCommand]
    private Task RestoreBooksAsync() => RestoreHoshiAsync(HoshiBackupTarget.Books);

    [RelayCommand]
    private Task BackupDictionariesAsync() => BackupHoshiAsync(HoshiBackupTarget.Dictionaries);

    [RelayCommand]
    private Task RestoreDictionariesAsync() => RestoreHoshiAsync(HoshiBackupTarget.Dictionaries);

    [RelayCommand]
    private async Task ExportTtuAsync()
    {
        if (IsBusy)
            return;
        var path = await _dialogService.SaveFilePickerAsync(
            $"hoshi_ttu_export_{Timestamp()}",
            ResourceStringHelper.GetString("BackupTtuFileType", "TTU backup"),
            ".zip");
        if (path == null)
            return;

        await RunAsync(
            ResourceStringHelper.GetString("BackupStatusExporting", "Exporting..."),
            async () =>
            {
                var progress = new Progress<string>(value => StatusText = ResourceStringHelper.FormatString(
                    "BackupStatusExportingProgressFormat",
                    "Exporting {0}",
                    value));
                await _backupService.ExportTtuBackupAsync(path, progress);
                _notifications.ShowSuccess(
                    ResourceStringHelper.GetString("BackupTtuExportedMessage", "TTU backup exported."),
                    ResourceStringHelper.GetString("BackupCompletedTitle", "Backup complete"));
            });
    }

    [RelayCommand]
    private async Task ImportTtuAsync()
    {
        if (IsBusy)
            return;
        var path = await _dialogService.OpenFilePickerAsync(".zip");
        if (path == null)
            return;

        await RunAsync(
            ResourceStringHelper.GetString("BackupStatusImporting", "Importing..."),
            async () =>
            {
                var progress = new Progress<string>(value => StatusText = ResourceStringHelper.FormatString(
                    "BackupStatusImportingProgressFormat",
                    "Importing {0}",
                    value));
                var result = await _backupService.ImportTtuBackupAsync(path, progress);
                _notifications.ShowSuccess(
                    ResourceStringHelper.FormatString(
                        "BackupTtuImportedMessageFormat",
                        "TTU backup imported: {0} added, {1} updated.",
                        result.AddedBooks,
                        result.UpdatedBooks),
                    ResourceStringHelper.GetString("BackupRestoreCompletedTitle", "Restore complete"));
            });
    }

    private async Task BackupHoshiAsync(HoshiBackupTarget target)
    {
        if (IsBusy)
            return;
        var collectionName = target == HoshiBackupTarget.Books ? "Books" : "Dictionaries";
        var path = await _dialogService.SaveFilePickerAsync(
            $"{collectionName}_{Timestamp()}",
            ResourceStringHelper.GetString("BackupHoshiFileType", "Niratan backup"),
            ".hoshi");
        if (path == null)
            return;

        await RunAsync(
            ResourceStringHelper.GetString("BackupStatusArchiving", "Archiving..."),
            async () =>
            {
                await _backupService.CreateHoshiBackupAsync(target, path);
                _notifications.ShowSuccess(
                    ResourceStringHelper.GetString("BackupCreatedMessage", "Backup created."),
                    ResourceStringHelper.GetString("BackupCompletedTitle", "Backup complete"));
            });
    }

    private async Task RestoreHoshiAsync(HoshiBackupTarget target)
    {
        if (IsBusy)
            return;
        var path = await _dialogService.OpenFilePickerAsync(".hoshi");
        if (path == null)
            return;

        await RunAsync(
            ResourceStringHelper.GetString("BackupStatusRestoring", "Restoring..."),
            async () =>
            {
                await _backupService.RestoreHoshiBackupAsync(target, path);
                _notifications.ShowSuccess(
                    ResourceStringHelper.GetString("BackupRestoredMessage", "Backup restored."),
                    ResourceStringHelper.GetString("BackupRestoreCompletedTitle", "Restore complete"));
            });
    }

    private async Task RunAsync(string status, Func<Task> action)
    {
        IsBusy = true;
        StatusText = status;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _notifications.ShowError(
                ex.Message,
                ResourceStringHelper.GetString("BackupErrorTitle", "Backup error"));
        }
        finally
        {
            StatusText = "";
            IsBusy = false;
        }
    }

    private static string Timestamp() =>
        DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
}
