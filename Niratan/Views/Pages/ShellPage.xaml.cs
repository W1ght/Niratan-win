using CommunityToolkit.Mvvm.Messaging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Niratan.Enums;
using Niratan.Messages;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class ShellPage : Page
{
    public ShellPageViewModel ViewModel { get; set; }

    public ShellPage()
    {
        InitializeComponent();
        ViewModel = App.GetService<ShellPageViewModel>();
        DataContext = ViewModel;

        var messenger = App.GetService<IMessenger>();

        messenger.Register<SwitchAppModeMessage>(
            this,
            (r, m) =>
            {
                var pageType = m.appMode switch
                {
                    AppMode.NovelReader => typeof(NovelReaderPage),
                    _ => typeof(NavigationPage),
                };
                var isReaderMode = m.appMode is AppMode.NovelReader;
                var transitionInfo =
                    isReaderMode
                        ? new SlideNavigationTransitionInfo()
                        {
                            Effect = SlideNavigationTransitionEffect.FromRight,
                        }
                        : new SlideNavigationTransitionInfo()
                        {
                            Effect = SlideNavigationTransitionEffect.FromLeft,
                        };

                ShellFrame.Navigate(pageType, m.Parameter, transitionInfo);
            }
        );
    }
}
