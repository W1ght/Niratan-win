using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
}
