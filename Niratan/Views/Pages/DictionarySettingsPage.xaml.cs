using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Helpers;
using Niratan.Models.Dictionary;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class DictionarySettingsPage : Page
{
    public DictionarySettingsPageViewModel ViewModel { get; set; }

    public DictionarySettingsPage()
    {
        ViewModel = App.GetService<DictionarySettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        DictionarySettingsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().GoBack();
    }

    private async void DeleteDictionary_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string dictName)
            return;

        await ViewModel.DeleteDictionaryCommand.ExecuteAsync(dictName);
    }

    private async void DictionaryEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.Tag is not string dictName)
            return;
        if (!toggle.IsLoaded)
            return;

        await ViewModel.SetDictionaryEnabledAsync(dictName, toggle.IsOn);
    }

    private void DictionaryType_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag })
            return;

        if (Enum.TryParse<DictionaryType>(tag, out var type)
            && ViewModel.SelectedDictionaryType != type)
        {
            ViewModel.SelectedDictionaryType = type;
        }
    }

    private async void DictionaryList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        await ViewModel.SaveDictionaryOrderAsync();
    }

    private async void DownloadRecommendedDictionaries_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.PrepareRecommendedDictionaries();
        await RecommendedDictionariesDialog.ShowAsync();
    }

    private async void RecommendedDictionariesDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        await ViewModel.DownloadRecommendedDictionariesAsync();
    }

    private async void UpdateDictionaries_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.CheckForUpdatesAsync();
        if (ViewModel.AvailableUpdates.Count > 0)
            await DictionaryUpdatesDialog.ShowAsync();
    }

    private async void DictionaryUpdatesDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        await ViewModel.UpdateSelectedDictionariesAsync();
    }

    private async void ConfigureCollapsedDictionaries_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.PrepareCollapsedDictionariesAsync();
        await CollapsedDictionariesDialog.ShowAsync();
    }

    private void CollapsedDictionariesDialog_PrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args) =>
        ViewModel.SaveCollapsedDictionaries();

    private async void CustomCss_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.LoadCustomCssDraft();
        var editor = new TextBox
        {
            AcceptsReturn = true,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MinWidth = 560,
            MinHeight = 420,
            Text = ViewModel.CustomCssDraft,
            TextWrapping = TextWrapping.NoWrap,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(editor, ScrollBarVisibility.Auto);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceStringHelper.GetString("DictionaryCustomCssDialogTitle", "Custom CSS"),
            Content = editor,
            PrimaryButtonText = ResourceStringHelper.GetString("DictionaryCustomCssDialogSave", "Save"),
            SecondaryButtonText = ResourceStringHelper.GetString("DictionaryCustomCssDialogReset", "Reset"),
            CloseButtonText = ResourceStringHelper.GetString("DictionaryCustomCssDialogCancel", "Cancel"),
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            editor.Text = "";
            args.Cancel = true;
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.CustomCssDraft = editor.Text;
            ViewModel.SaveCustomCss();
        }
    }
}
