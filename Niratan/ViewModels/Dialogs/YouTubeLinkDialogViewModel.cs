using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models;
using Niratan.Services.Video;

namespace Niratan.ViewModels.Dialogs;

public partial class YouTubeLinkDialogViewModel : ObservableObject
{
    private readonly IRemoteVideoResolver _resolver;

    [ObservableProperty]
    public partial string Url { get; set; } = "";

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = "";

    [ObservableProperty]
    public partial bool IsResolving { get; set; }

    public YouTubeLinkDialogViewModel(IRemoteVideoResolver resolver)
    {
        _resolver = resolver;
    }

    public async Task<ResolvedRemoteVideoSource?> ResolveAsync(CancellationToken ct)
    {
        ErrorMessage = "";
        IsResolving = true;
        try
        {
            return await _resolver.ResolveAsync(Url, ct: ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteVideoResolverException ex)
        {
            ErrorMessage = ex.Error switch
            {
                RemoteVideoResolverError.UnsupportedUrl => ResourceStringHelper.GetString("YouTubeInvalidUrl", "Enter a valid YouTube video link."),
                RemoteVideoResolverError.NoPlayableStream => ResourceStringHelper.GetString("YouTubeNoPlayableStream", "No compatible stream up to 1080p is available."),
                RemoteVideoResolverError.ContentUnavailable or RemoteVideoResolverError.SignInRequired or RemoteVideoResolverError.RegionRestricted => ResourceStringHelper.GetString("YouTubeRestricted", "This video requires access that Niratan does not support."),
                _ => ResourceStringHelper.GetString("YouTubeResolveFailed", "The YouTube video could not be resolved. Try again later."),
            };
            return null;
        }
        finally
        {
            IsResolving = false;
        }
    }
}
