using System.Collections.Generic;

namespace Niratan.ViewModels.Components;

public sealed class MangaDiscoverSectionViewModel
{
    public MangaDiscoverSectionViewModel(
        string feedId,
        string title,
        IReadOnlyList<MangaDiscoveryCardViewModel> items)
    {
        FeedId = feedId;
        Title = title;
        Items = items;
    }

    public string FeedId { get; }
    public string Title { get; }
    public IReadOnlyList<MangaDiscoveryCardViewModel> Items { get; }
}
