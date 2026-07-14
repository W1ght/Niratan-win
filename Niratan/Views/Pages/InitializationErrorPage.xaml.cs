using System;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Helpers;

namespace Niratan.Views.Pages;

public sealed partial class InitializationErrorPage : Page
{
    public InitializationErrorPage()
    {
        InitializeComponent();

        Loaded += (_, _) => App.MainWindow?.SetTitleBar(AppTitleBar);
        VersionTextBlock.Text = $"Version {AppInfoHelper.Version}";
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is Exception ex)
        {
            ErrorMessageTextBlock.Text = ex.ToString();
        }
    }
}
