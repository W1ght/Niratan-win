using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Niratan.Messages;
using Niratan.Models;

namespace Niratan.ViewModels.Pages;

public partial class ShellPageViewModel : ObservableRecipient, IRecipient<ShowNotificationMessage>
{
    public ObservableCollection<NotificationModel> Notifications { get; } = new();

    public ShellPageViewModel(IMessenger messenger)
        : base(messenger)
    {
        IsActive = true;
    }

    public async void Receive(ShowNotificationMessage message)
    {
        var notification = message.Notification;

        Notifications.Add(notification);

        await Task.Delay(10000);
        Notifications.Remove(notification);
    }
}
