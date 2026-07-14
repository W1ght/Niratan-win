using Microsoft.UI.Xaml.Controls;

namespace Niratan.Views.Pages;

public sealed partial class ErrorLogsPage : Page
{
    public ViewModels.Pages.LogsPageViewModel ViewModel { get; set; }

    public ErrorLogsPage()
    {
        ViewModel = App.GetService<ViewModels.Pages.LogsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }
}
