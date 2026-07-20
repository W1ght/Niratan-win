using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using Niratan.Models.Shortcuts;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class KeyboardShortcutsSettingsPage : Page
{
    public KeyboardShortcutsSettingsPageViewModel ViewModel { get; set; }

    public KeyboardShortcutsSettingsPage()
    {
        ViewModel = App.GetService<KeyboardShortcutsSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
        ShortcutCaptureLayer.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(ShortcutCaptureLayer_KeyDown),
            true);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        KeyboardShortcutsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ViewModel.CancelRecording();
        base.OnNavigatedFrom(e);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        var navigation = App.GetService<INavigationService>();
        if (!navigation.GoBack())
            navigation.Navigate(typeof(SettingsPage));
    }

    private void DictionaryEntryJumpCountNumberBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        if (!double.IsNaN(args.NewValue))
            ViewModel.SetDictionaryEntryJumpCount(args.NewValue);
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModel.IsRecording) || !ViewModel.IsRecording)
            return;

        DispatcherQueue.TryEnqueue(() => ShortcutCaptureLayer.Focus(FocusState.Programmatic));
    }

    private void ShortcutCaptureLayer_KeyDown(object sender, KeyRoutedEventArgs e)
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
