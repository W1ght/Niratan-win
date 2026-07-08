using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Hoshi.Models.Shortcuts;
using Hoshi.Services.UI;
using Hoshi.ViewModels.Pages;

namespace Hoshi.Views.Pages;

public sealed partial class KeyboardShortcutsSettingsPage : Page
{
    public KeyboardShortcutsSettingsPageViewModel ViewModel { get; set; }

    public KeyboardShortcutsSettingsPage()
    {
        ViewModel = App.GetService<KeyboardShortcutsSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        KeyboardShortcutsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigation = App.GetService<INavigationService>();
        if (!navigation.GoBack())
            navigation.Navigate(typeof(SettingsPage));
    }

    private void RecordShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShortcutRowViewModel row)
            return;

        ViewModel.StartRecording(row);
        KeyboardShortcutsRoot.Focus(FocusState.Programmatic);
    }

    private void ResetShortcutButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ShortcutRowViewModel row)
            ViewModel.ResetShortcut(row);
    }

    private void KeyboardShortcutsRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!ViewModel.IsRecording)
            return;

        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.CancelRecording();
            e.Handled = true;
            return;
        }

        if (ShortcutInputMapper.IsModifierKey(e.Key))
            return;

        ViewModel.CaptureShortcut(KeyboardShortcutBinding.FromVirtualKey(
            e.Key,
            ShortcutInputMapper.GetCurrentModifiers()));
        e.Handled = true;
    }
}
