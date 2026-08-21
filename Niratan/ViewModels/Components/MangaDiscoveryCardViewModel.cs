using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Models.Manga;

namespace Niratan.ViewModels.Components;

public sealed partial class MangaDiscoveryCardViewModel : ObservableObject
{
    private int _posterLoadState;

    public MangaDiscoveryCardViewModel(
        MangaDiscoveryItem item,
        Func<Task> open)
    {
        Item = item;
        OpenCommand = new AsyncRelayCommand(open);
    }

    public MangaDiscoveryItem Item { get; }
    public string Provider => Item.ProviderId;
    public string Id => Item.ProviderItemId;
    public string Title => Item.Title;
    public string SourceText => Item.ProviderId switch
    {
        "bangumi" => "Bangumi",
        "anilist" => "AniList",
        _ => Item.ProviderId,
    };
    public string FactsText
    {
        get
        {
            var facts = Item.Year is int year
                ? year.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            if (Item.Score is double score)
            {
                if (facts.Length > 0)
                    facts += " · ";
                facts += "★ " + score.ToString("0.0", CultureInfo.CurrentCulture);
            }
            return facts;
        }
    }
    public string AutomationId => $"MangaDiscoveryCard_{Provider}_{Id}";
    public IAsyncRelayCommand OpenCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPoster))]
    public partial BitmapImage? PosterImage { get; set; }

    public bool HasPoster => PosterImage is not null;

    public void SetPosterPath(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            PosterImage = new BitmapImage(new Uri(Path.GetFullPath(path)));
    }

    internal bool TryBeginPosterLoad() =>
        Interlocked.CompareExchange(ref _posterLoadState, 1, 0) == 0;

    internal void ResetPosterLoad() =>
        Interlocked.Exchange(ref _posterLoadState, 0);
}
