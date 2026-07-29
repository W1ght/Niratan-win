using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Manga;

public sealed partial class MangaSourceSettingsContent : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(MangaLibraryPageViewModel),
            typeof(MangaSourceSettingsContent),
            new PropertyMetadata(null, OnViewModelChanged));

    public MangaLibraryPageViewModel? ViewModel
    {
        get => (MangaLibraryPageViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public MangaSourceSettingsContent()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MangaSourceSettingsContent content)
            content.DataContext = args.NewValue;
    }

    private void MangaSourcesSecretBox_PasswordChanged(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && ViewModel is not null)
            ViewModel.Secret = passwordBox.Password;
    }
}
