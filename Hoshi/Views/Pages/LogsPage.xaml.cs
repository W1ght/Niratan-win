using Microsoft.UI.Xaml.Controls;

namespace Hoshi.Views.Pages;

public sealed partial class LogsPage : Page
{
    public ViewModels.Pages.LogsPageViewModel ViewModel { get; set; }

    public LogsPage()
    {
        ViewModel = App.GetService<ViewModels.Pages.LogsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }
}
