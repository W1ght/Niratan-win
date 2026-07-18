using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Niratan.ViewModels.Components;

public sealed partial class NovelShelfSectionViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public bool IsDerived { get; init; }
    public bool IsUnshelved { get; init; }
    public bool IsRemote { get; init; }
    public bool CanCollapse { get; init; }
    public ObservableCollection<NovelBookItemViewModel> Books { get; init; } = [];
    public ObservableCollection<RemoteNovelBookItemViewModel> RemoteBooks { get; init; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsFullContent))]
    [NotifyPropertyChangedFor(nameof(ShowsCollapsedPreview))]
    [NotifyPropertyChangedFor(nameof(CollapseGlyph))]
    public partial bool IsCollapsed { get; set; }

    public bool ShowsLocalBooks => !IsRemote;
    public bool ShowsRemoteBooks => IsRemote;
    public bool ShowsFullContent => !CanCollapse || !IsCollapsed;
    public bool ShowsCollapsedPreview => CanCollapse && IsCollapsed && BookCount > 0;
    public int BookCount => IsRemote ? RemoteBooks.Count : Books.Count;
    public string CollapseGlyph => IsCollapsed ? "\uE76C" : "\uE70D";

    [RelayCommand]
    private void ToggleCollapse()
    {
        if (CanCollapse)
            IsCollapsed = !IsCollapsed;
    }

    [RelayCommand]
    private void Expand()
    {
        if (CanCollapse)
            IsCollapsed = false;
    }
}
