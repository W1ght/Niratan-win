using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Niratan.ViewModels.Pages;
using Niratan.ViewModels.Components;
using Windows.System;
using DispatcherTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Niratan.Views.Pages;

public enum DownloadsPageSection
{
    Discovery,
    Tasks,
    Subscriptions,
    Settings,
}

public sealed partial class DownloadsPage : Page, IDisposable
{
    private DispatcherTimer? _refreshTimer;
    private bool _disposed;
    private bool _taskDetailsDialogOpen;
    private DownloadsPageSection _requestedSection = DownloadsPageSection.Discovery;

    public DownloadsPageViewModel ViewModel { get; }

    public DownloadsPage()
    {
        ViewModel = App.GetService<DownloadsPageViewModel>();
        InitializeComponent();
        // The default ContentDialogMaxWidth is too narrow for torrent paths, hashes,
        // and tracker URLs. Keep the panel wide while allowing the host window to
        // constrain it on smaller displays.
        TaskDetailsDialog.Resources["ContentDialogMaxWidth"] = 1180d;
        DataContext = ViewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ViewModel.TaskDetailsRequested += OnTaskDetailsRequested;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _requestedSection = e.Parameter switch
        {
            DownloadsPageSection section => section,
            string route when Enum.TryParse<DownloadsPageSection>(route, true, out var parsedSection) => parsedSection,
            _ => DownloadsPageSection.Discovery,
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.InitializeAsync();
        SelectSection(_requestedSection);
        _refreshTimer ??= DispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromSeconds(4);
        _refreshTimer.IsRepeating = true;
        _refreshTimer.Tick -= RefreshTimer_Tick;
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();
    }

    private void RefreshTimer_Tick(DispatcherTimer sender, object args)
    {
        if (ViewModel.IsTasksVisible)
            ViewModel.RefreshTasksCommand.Execute(null);
    }

    private void DownloadsSectionNavigation_ItemInvoked(
        NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item)
            return;

        switch (item.Tag as string)
        {
            case "tasks":
                ViewModel.SelectTasksCommand.Execute(null);
                break;
            case "subscriptions":
                ViewModel.SelectSubscriptionsCommand.Execute(null);
                break;
            case "settings":
                ViewModel.SelectSettingsCommand.Execute(null);
                break;
            default:
                ViewModel.SelectDiscoveryCommand.Execute(null);
                break;
        }
    }

    private void SelectSection(DownloadsPageSection section)
    {
        switch (section)
        {
            case DownloadsPageSection.Tasks:
                DownloadsSectionNavigation.SelectedItem = DownloadsTasksNavItem;
                ViewModel.SelectTasksCommand.Execute(null);
                break;
            case DownloadsPageSection.Subscriptions:
                DownloadsSectionNavigation.SelectedItem = DownloadsSubscriptionsNavItem;
                ViewModel.SelectSubscriptionsCommand.Execute(null);
                break;
            case DownloadsPageSection.Settings:
                DownloadsSectionNavigation.SelectedItem = DownloadsSettingsNavItem;
                ViewModel.SelectSettingsCommand.Execute(null);
                break;
            default:
                DownloadsSectionNavigation.SelectedItem = DownloadsDiscoveryNavItem;
                ViewModel.SelectDiscoveryCommand.Execute(null);
                break;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.SearchCommand.CanExecute(null))
            ViewModel.SearchCommand.Execute(null);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.PasswordDraft = PasswordBox.Password;

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) =>
        ViewModel.ApiKeyDraft = ApiKeyBox.Password;

    private void TaskList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QbittorrentTorrentViewModel task
            && ViewModel.ShowTaskDetailsCommand.CanExecute(task))
        {
            ViewModel.ShowTaskDetailsCommand.Execute(task);
        }
    }

    private async void OnTaskDetailsRequested(object? sender, EventArgs e)
    {
        if (_disposed || _taskDetailsDialogOpen)
            return;

        _taskDetailsDialogOpen = true;
        TaskDetailsDialog.XamlRoot = XamlRoot;
        try
        {
            await TaskDetailsDialog.ShowAsync();
        }
        finally
        {
            _taskDetailsDialogOpen = false;
        }
    }

    private async void DeleteSelectedTaskButton_Click(object sender, RoutedEventArgs e)
    {
        TaskDetailsDialog.Hide();
        await ViewModel.DeleteSelectedTaskCommand.ExecuteAsync(null);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        ViewModel.TaskDetailsRequested -= OnTaskDetailsRequested;
        if (_refreshTimer is not null)
        {
            _refreshTimer.Tick -= RefreshTimer_Tick;
            _refreshTimer.Stop();
        }
        ViewModel.Dispose();
    }
}
