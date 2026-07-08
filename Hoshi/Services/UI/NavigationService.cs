using System;
using Microsoft.UI.Xaml.Controls;
using Hoshi.Enums;
using Hoshi.Services.UI;
using Hoshi.Views.Pages;

namespace Hoshi.Services;

internal sealed class NavigationService : INavigationService
{
    private Frame? _frame;

    public AppPage CurrentPage =>
        _frame == null
            ? AppPage.Other
            : _frame.CurrentSourcePageType switch
            {
                Type t when t == typeof(NovelLibraryPage) => AppPage.NovelLibraryPage,
                Type t when t == typeof(VideoLibraryPage) => AppPage.VideoLibraryPage,
                Type t when t == typeof(NovelLookupPage) => AppPage.NovelLookupPage,
                Type t when t == typeof(ReaderAppearanceSettingsPage) => AppPage.ReaderAppearanceSettingsPage,
                Type t when t == typeof(AdvancedSettingsPage) => AppPage.AdvancedSettingsPage,
                Type t when t == typeof(SasayakiSettingsPage) => AppPage.SasayakiSettingsPage,
                Type t when t == typeof(StatisticsSettingsPage) => AppPage.StatisticsSettingsPage,
                Type t when t == typeof(VideoSettingsPage) => AppPage.VideoSettingsPage,
                Type t when t == typeof(KeyboardShortcutsSettingsPage) => AppPage.KeyboardShortcutsSettingsPage,
                _ => AppPage.Other,
            };

    public void Initialize(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public bool CanGoBack => _frame != null && _frame.CanGoBack;

    public void Navigate(Type pageType, object? parameter = null)
    {
        if (_frame == null)
            throw new InvalidOperationException(
                "NavigationService not initialized. Call Initialize(Frame) before navigating."
            );

        if (_frame.CurrentSourcePageType != pageType)
            _frame.Navigate(pageType, parameter);
    }

    public bool GoBack()
    {
        if (!CanGoBack)
            return false;

        _frame!.GoBack();
        return true;
    }
}
