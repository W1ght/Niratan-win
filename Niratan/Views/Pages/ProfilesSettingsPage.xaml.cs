using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Niratan.Services.UI;
using Niratan.ViewModels.Pages;
using Niratan.Helpers;

namespace Niratan.Views.Pages;

public sealed partial class ProfilesSettingsPage : Page
{
    public ProfilesSettingsPageViewModel ViewModel { get; }

    public ProfilesSettingsPage()
    {
        ViewModel = App.GetService<ProfilesSettingsPageViewModel>();
        InitializeComponent();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ProfilesSettingsBackButton.Visibility = e.Parameter is SettingsNavigationMode.Embedded
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        App.GetService<INavigationService>().GoBack();
    }

    private void RenameProfileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: ProfileOption option })
            ViewModel.BeginRenameProfileCommand.Execute(option);
    }

    private async void DeleteProfileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: ProfileOption option } || !option.CanDelete)
            return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = ResourceStringHelper.FormatString(
                "ProfilesDeleteConfirmationTitle",
                "Delete \"{0}\"?",
                option.Name),
            Content = ResourceStringHelper.GetString(
                "ProfilesDeleteConfirmationMessage",
                "This Profile and its settings will be removed."),
            PrimaryButtonText = ResourceStringHelper.GetString("ProfilesDeleteButton", "Delete"),
            CloseButtonText = ResourceStringHelper.GetString("ProfilesCancelButton", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.DeleteProfileCommand.ExecuteAsync(option);
    }
}
