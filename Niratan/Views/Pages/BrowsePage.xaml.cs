using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Niratan.ViewModels.Components;
using Niratan.ViewModels.Pages;
using Windows.System;

namespace Niratan.Views.Pages;

public sealed partial class BrowsePage : Page
{
    public MangaLibraryPageViewModel ViewModel { get; }
    private readonly HashSet<ScrollViewer> _horizontalMangaLists = [];
    private readonly PointerEventHandler _horizontalMangaWheelHandler;

    public BrowsePage()
    {
        _horizontalMangaWheelHandler = HorizontalMangaList_PointerWheelChanged;
        InitializeComponent();
        ViewModel = App.GetService<MangaLibraryPageViewModel>();
        DataContext = ViewModel;
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        var section = e.Parameter is MangaHomeSection requestedSection
            ? requestedSection
            : MangaHomeSection.Discover;
        await ViewModel.InitializeBrowseAsync(section);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        DetachHorizontalMangaLists();
        ViewModel.OnNavigatedFrom();
        base.OnNavigatedFrom(e);
    }

    private async void BrowseSourceRow_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is FrameworkElement
        {
                DataContext: MangaBrowseSourceItemViewModel item
        })
        {
            await item.EnsureIconAsync();
        }
    }

    private void BrowseSourceIcon_ImageFailed(
        object sender,
        ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement
            {
                DataContext: MangaBrowseSourceItemViewModel item
            })
        {
            item.IconImage = null;
        }
    }

    private void MangaBrowseBookshelf_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        var preloadStart = Math.Max(0, ViewModel.BrowseBooks.Count - 6);
        if (args.Index < preloadStart)
            return;

        var command = ViewModel.LoadNextBrowsePageCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private async void MangaDiscoveryCard_ElementPrepared(
        ItemsRepeater sender,
        ItemsRepeaterElementPreparedEventArgs args)
    {
        var card = args.Element is FrameworkElement
            {
                DataContext: MangaDiscoveryCardViewModel boundCard
            }
            ? boundCard
            : args.Index >= 0 && args.Index < sender.ItemsSourceView.Count
                ? sender.ItemsSourceView.GetAt(args.Index)
                    as MangaDiscoveryCardViewModel
                : null;
        if (card is not null)
            await ViewModel.EnsureMangaDiscoveryPosterAsync(card);
    }

    private void MangaDiscoverSearchTextBox_KeyDown(
        object sender,
        KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;
        e.Handled = true;
        if (ViewModel.SearchMangaDiscoverCommand.CanExecute(null))
            ViewModel.SearchMangaDiscoverCommand.Execute(null);
    }

    private void MangaDiscoverContentScrollViewer_ViewChanged(
        object sender,
        ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate)
            return;

        var scrollViewer = (ScrollViewer)sender;
        if (scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset > 700)
            return;

        var command = ViewModel.LoadMoreMangaDiscoverCommand;
        if (command.CanExecute(null))
            command.Execute(null);
    }

    private void HorizontalMangaList_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer
            || !_horizontalMangaLists.Add(scrollViewer))
        {
            return;
        }

        scrollViewer.AddHandler(
            UIElement.PointerWheelChangedEvent,
            _horizontalMangaWheelHandler,
            true);
    }

    private void HorizontalMangaList_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer
            || !_horizontalMangaLists.Remove(scrollViewer))
        {
            return;
        }

        scrollViewer.RemoveHandler(
            UIElement.PointerWheelChangedEvent,
            _horizontalMangaWheelHandler);
    }

    private void HorizontalMangaList_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer horizontalScrollViewer)
            return;

        var delta = e.GetCurrentPoint(horizontalScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            var verticalScrollViewer = FindVerticalScrollViewer(
                VisualTreeHelper.GetParent(horizontalScrollViewer));
            if (verticalScrollViewer is null)
                return;

            var verticalTarget = Math.Clamp(
                verticalScrollViewer.VerticalOffset - delta,
                0,
                verticalScrollViewer.ScrollableHeight);
            verticalScrollViewer.ChangeView(
                null,
                verticalTarget,
                null,
                disableAnimation: false);
            e.Handled = true;
            return;
        }

        if (horizontalScrollViewer.ScrollableWidth <= 0)
            return;

        var target = Math.Clamp(
            horizontalScrollViewer.HorizontalOffset - delta,
            0,
            horizontalScrollViewer.ScrollableWidth);
        if (Math.Abs(target - horizontalScrollViewer.HorizontalOffset) < 0.5)
        {
            e.Handled = true;
            return;
        }

        horizontalScrollViewer.ChangeView(
            target,
            null,
            null,
            disableAnimation: false);
        e.Handled = true;
    }

    private static ScrollViewer? FindVerticalScrollViewer(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer
                && scrollViewer.ScrollableHeight > 0)
            {
                return scrollViewer;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void DetachHorizontalMangaLists()
    {
        foreach (var scrollViewer in _horizontalMangaLists)
        {
            scrollViewer.RemoveHandler(
                UIElement.PointerWheelChangedEvent,
                _horizontalMangaWheelHandler);
        }

        _horizontalMangaLists.Clear();
    }
}
