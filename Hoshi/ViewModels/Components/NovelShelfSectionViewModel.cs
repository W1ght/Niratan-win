using System.Collections.ObjectModel;

namespace Hoshi.ViewModels.Components;

public sealed record NovelShelfSectionViewModel(
    string Id,
    string DisplayName,
    bool IsDerived,
    bool IsUnshelved,
    ObservableCollection<NovelBookItemViewModel> Books);
