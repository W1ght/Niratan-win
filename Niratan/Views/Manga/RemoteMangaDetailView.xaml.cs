using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Manga;

public sealed partial class RemoteMangaDetailView : UserControl
{
    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(
            nameof(ViewModel),
            typeof(MangaLibraryPageViewModel),
            typeof(RemoteMangaDetailView),
            new PropertyMetadata(null, OnViewModelChanged));

    public MangaLibraryPageViewModel? ViewModel
    {
        get => (MangaLibraryPageViewModel?)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public RemoteMangaDetailView()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is RemoteMangaDetailView details)
            details.DataContext = args.NewValue;
    }

    private async void RemoteMangaExtensionsComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (sender is not ComboBox comboBox
            || comboBox.SelectedItem is not RemoteMangaExtensionOptionViewModel option
            || option.IsSelected
            || ViewModel is null)
        {
            return;
        }

        await ViewModel.SelectRemoteMangaExtensionCommand.ExecuteAsync(option);

        if (ViewModel.SelectedRemoteMangaDetails is { } details)
            comboBox.SelectedItem = details.SelectedExtension;
    }
}
