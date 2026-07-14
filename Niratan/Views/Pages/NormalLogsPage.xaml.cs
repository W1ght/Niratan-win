using Microsoft.UI.Xaml.Controls;

namespace Niratan.Views.Pages;

public sealed partial class NormalLogsPage : Page
{
    public ViewModels.Pages.LogsPageViewModel ViewModel { get; set; }

    public NormalLogsPage()
    {
        ViewModel = App.GetService<ViewModels.Pages.LogsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }
}
