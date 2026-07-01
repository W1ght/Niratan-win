using CommunityToolkit.Mvvm.ComponentModel;

namespace Hoshi.ViewModels.Pages;

public partial class NovelLookupPageViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Enter text to look up.";

    [ObservableProperty]
    public partial bool IsLookupInProgress { get; set; }
}
