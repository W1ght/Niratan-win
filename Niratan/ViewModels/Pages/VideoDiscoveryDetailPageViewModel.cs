using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Niratan.Helpers;
using Niratan.Models.Video;
using Niratan.Services.UI;
using Niratan.Services.Video;
using Niratan.ViewModels.Components;
using Niratan.Views.Pages;

namespace Niratan.ViewModels.Pages;

public partial class VideoDiscoveryDetailPageViewModel : ObservableObject, IDisposable
{
    private static readonly VideoMetadataCandidate EmptyIdentity = new(
        "discovery",
        "empty",
        VideoMetadataMediaKind.Series,
        "",
        null,
        null,
        null,
        null,
        null,
        [],
        System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
        null);

    private readonly IVideoDiscoveryService _discovery;
    private readonly INyaaSubscriptionService _subscriptions;
    private readonly INavigationService _navigation;
    private CancellationTokenSource _cts = new();
    private bool _disposed;

    [ObservableProperty]
    public partial VideoDiscoveryNavigationTarget Target { get; set; } = new(
        EmptyIdentity,
        new VideoDiscoveryArtwork(null, null, null));

    [ObservableProperty]
    public partial VideoDiscoveryDetailsViewModel Details { get; set; } = new(EmptyIdentity);

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubscriptionButtonText))]
    public partial bool IsSubscribed { get; set; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public string SubscriptionButtonText => IsSubscribed
        ? ResourceStringHelper.GetString("DiscoverSubscriptionManageButton", "Manage subscription")
        : ResourceStringHelper.GetString("DiscoverSubscribeButton", "Subscribe");

    public VideoDiscoveryNavigationTarget AcquisitionTarget
    {
        get
        {
            var identity = Details.Identity with
            {
                PosterUrl = Details.Identity.PosterUrl ?? Target.Identity.PosterUrl,
                BackdropUrl = Details.Identity.BackdropUrl ?? Target.Identity.BackdropUrl,
            };
            return new VideoDiscoveryNavigationTarget(
                identity,
                Details.Artwork,
                Details.Overview,
                Details.Metadata.CommunityRating);
        }
    }

    public VideoDiscoveryDetailPageViewModel(
        IVideoDiscoveryService discovery,
        INyaaSubscriptionService subscriptions,
        INavigationService navigation)
    {
        _discovery = discovery;
        _subscriptions = subscriptions;
        _navigation = navigation;
    }

    public async Task InitializeAsync(VideoDiscoveryNavigationTarget target)
    {
        if (_disposed)
            return;
        _cts.Cancel();
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        Target = target;
        Details = new VideoDiscoveryDetailsViewModel(target);
        IsSubscribed = _subscriptions.IsSubscribed(target.Identity);
        ErrorMessage = null;
        IsLoading = true;
        try
        {
            var result = await _discovery.GetDetailsAsync(target.Identity, _cts.Token);
            if (result.IsCancelled)
                return;
            if (!result.IsSuccess || result.Value is null)
            {
                ErrorMessage = result.Error;
                return;
            }

            Details = new VideoDiscoveryDetailsViewModel(result.Value, target.Artwork);
            OnPropertyChanged(nameof(AcquisitionTarget));
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public void OpenSubscriptions() =>
        _navigation.Navigate(typeof(DownloadsPage), DownloadsPageSection.Subscriptions);

    public void OnNavigatedFrom() => _cts.Cancel();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
