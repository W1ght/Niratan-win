using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Manga;

public sealed partial class MihonExtensionBrowser : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(MangaLibraryPageViewModel),
            typeof(MihonExtensionBrowser),
            new PropertyMetadata(null, OnViewModelChanged));

    public MangaLibraryPageViewModel? ViewModel
    {
        get => (MangaLibraryPageViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MihonExtensionBrowser()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MihonExtensionBrowser browser)
            browser.DataContext = args.NewValue;
    }

    private async void MihonRepositorySourceRow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: MihonRepositorySourceItemViewModel item
            })
        {
            await item.EnsureIconAsync();
        }
    }

    private async void MihonRepositorySourceRow_DataContextChanged(
        FrameworkElement sender,
        DataContextChangedEventArgs args)
    {
        if (args.NewValue is MihonRepositorySourceItemViewModel item)
            await item.EnsureIconAsync();
    }

    private void MihonRepositorySourceIcon_ImageFailed(
        object sender,
        ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: MihonRepositorySourceItemViewModel item
            })
        {
            item.IconImage = null;
        }
    }
}
