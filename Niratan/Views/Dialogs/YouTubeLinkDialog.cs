using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Niratan.Helpers;
using Niratan.Helpers.UI.Converters;
using Niratan.Models;
using Niratan.ViewModels.Dialogs;

namespace Niratan.Views.Dialogs;

internal sealed class YouTubeLinkDialog
{
    private readonly YouTubeLinkDialogViewModel _viewModel;
    private readonly ContentDialog _dialog;
    private readonly CancellationTokenSource _cts = new();
    private ResolvedRemoteVideoSource? _result;

    public YouTubeLinkDialog(YouTubeLinkDialogViewModel viewModel)
    {
        _viewModel = viewModel;
        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed),
            TextWrapping = TextWrapping.Wrap,
        };
        error.SetBinding(TextBlock.TextProperty, new Binding
        {
            Path = new PropertyPath(nameof(YouTubeLinkDialogViewModel.ErrorMessage)),
            Source = viewModel,
            Mode = BindingMode.OneWay,
        });

        var input = new TextBox
        {
            Header = ResourceStringHelper.GetString("YouTubeDialogInputHeader", "YouTube video link"),
            PlaceholderText = "https://www.youtube.com/watch?v=...",
        };
        input.SetBinding(TextBox.TextProperty, new Binding
        {
            Path = new PropertyPath(nameof(YouTubeLinkDialogViewModel.Url)),
            Source = viewModel,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged,
        });
        AutomationProperties.SetAutomationId(input, "YouTubeLinkTextBox");

        var progress = new ProgressRing { Width = 22, Height = 22 };
        progress.SetBinding(ProgressRing.IsActiveProperty, new Binding
        {
            Path = new PropertyPath(nameof(YouTubeLinkDialogViewModel.IsResolving)),
            Source = viewModel,
            Mode = BindingMode.OneWay,
        });

        var progressRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                progress,
                new TextBlock
                {
                    Text = ResourceStringHelper.GetString(
                        "YouTubeDialogResolving",
                        "Resolving video, qualities, and publisher subtitles..."),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };
        progressRow.SetBinding(UIElement.VisibilityProperty, new Binding
        {
            Path = new PropertyPath(nameof(YouTubeLinkDialogViewModel.IsResolving)),
            Source = viewModel,
            Mode = BindingMode.OneWay,
            Converter = new BooleanToVisibilityConverter(),
        });

        _dialog = new ContentDialog
        {
            Title = ResourceStringHelper.GetString("YouTubeDialogTitle", "Add / open YouTube link"),
            PrimaryButtonText = ResourceStringHelper.GetString("YouTubeDialogOpen", "Open"),
            SecondaryButtonText = ResourceStringHelper.GetString("YouTubeDialogCancel", "Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                MinWidth = 440,
                Spacing = 12,
                Children =
                {
                    new InfoBar
                    {
                        IsOpen = true,
                        IsClosable = false,
                        Severity = InfoBarSeverity.Informational,
                        Title = ResourceStringHelper.GetString("YouTubeDialogExperimentalTitle", "Experimental"),
                        Message = ResourceStringHelper.GetString(
                            "YouTubeDialogExperimentalMessage",
                            "YouTube support uses an unofficial interface and may stop working when YouTube changes."),
                    },
                    input,
                    progressRow,
                    error,
                },
            },
        };
        AutomationProperties.SetAutomationId(_dialog, "AddYouTubeLinkDialog");
        _dialog.PrimaryButtonClick += OnPrimaryButtonClick;
        _dialog.SecondaryButtonClick += OnSecondaryButtonClick;
    }

    public async Task<ResolvedRemoteVideoSource?> ShowAsync(XamlRoot xamlRoot)
    {
        _dialog.XamlRoot = xamlRoot;
        await _dialog.ShowAsync();
        _dialog.PrimaryButtonClick -= OnPrimaryButtonClick;
        _dialog.SecondaryButtonClick -= OnSecondaryButtonClick;
        _cts.Dispose();
        return _result;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        args.Cancel = true;
        try
        {
            sender.IsPrimaryButtonEnabled = false;
            _result = await _viewModel.ResolveAsync(_cts.Token);
            args.Cancel = _result == null;
        }
        catch (OperationCanceledException)
        {
            args.Cancel = false;
        }
        finally
        {
            sender.IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void OnSecondaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        _cts.Cancel();
        if (_viewModel.IsResolving)
            args.Cancel = true;
    }
}
