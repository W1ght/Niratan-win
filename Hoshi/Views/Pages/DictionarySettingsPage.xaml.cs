using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class DictionarySettingsPage : Page
{
    public DictionarySettingsPageViewModel ViewModel { get; set; }

    public DictionarySettingsPage()
    {
        ViewModel = App.GetService<DictionarySettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
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

    private async void DictionaryList_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        await ViewModel.SaveDictionaryOrderAsync();
    }
}
