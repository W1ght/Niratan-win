using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
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
}
