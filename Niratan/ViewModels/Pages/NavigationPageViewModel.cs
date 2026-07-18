using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Messages;
using Niratan.Services.UI;

namespace Niratan.ViewModels.Pages;

public partial class NavigationPageViewModel
    : ObservableObject,
        IRecipient<NavigateMessage>
{
    private readonly INavigationService _navigationService;
    private readonly IMessenger _messenger;

    public bool IsBackEnabled => _navigationService.CanGoBack;

    public NavigationPageViewModel(
        INavigationService navigationService,
        IMessenger messenger)
    {
        _navigationService = navigationService;
        _messenger = messenger;
        _messenger.RegisterAll(this);
    }

    public void Receive(NavigateMessage message) => OnNavigate(message);

    [RelayCommand]
    private void OnNavigate(NavigateMessage request)
    {
        if (request.PageType == null)
            return;

        _navigationService.Navigate(request.PageType, request.Parameter);
        OnPropertyChanged(nameof(IsBackEnabled));
    }

    [RelayCommand]
    private void OnBack()
    {
        if (_navigationService.GoBack())
            OnPropertyChanged(nameof(IsBackEnabled));
    }
}
