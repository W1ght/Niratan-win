using System.Collections.ObjectModel;

namespace Hoshi.ViewModels.Components;

public sealed class NovelShelfSectionViewModel
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDerived { get; init; }
    public bool IsUnshelved { get; init; }
    public bool IsRemote { get; init; }
    public ObservableCollection<NovelBookItemViewModel> Books { get; init; } = [];
    public ObservableCollection<RemoteNovelBookItemViewModel> RemoteBooks { get; init; } = [];
    public bool ShowsLocalBooks => !IsRemote;
    public bool ShowsRemoteBooks => IsRemote;
}
