---
name: mvvm
description: CommunityToolkit.Mvvm patterns for this project — ObservableObject, ObservableProperty, RelayCommand, Messenger
---

# MVVM Patterns

This project uses [CommunityToolkit.Mvvm](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/) with source generators.

## ViewModel Template

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Hoshi.Services.XXX;

namespace Hoshi.ViewModels.Pages;

public partial class FooViewModel : ObservableObject
{
    private readonly IBarService _barService;

    // Observable properties (source-generated)
    [ObservableProperty]
    public partial string SomeText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    // Constructor injection (services registered in App.xaml.cs)
    public FooViewModel(IBarService barService)
    {
        _barService = barService;
    }

    // Relay commands (source-generated)
    [RelayCommand]
    private async Task DoSomethingAsync()
    {
        IsLoading = true;
        try
        {
            SomeText = await _barService.GetDataAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    // Parameterized command
    [RelayCommand]
    private void SelectItem(string itemId)
    {
        // ...
    }
}
```

## Rules

- ViewModel inherits `ObservableObject`
- Use `[ObservableProperty]` + `partial` property (don't write `OnPropertyChanged` manually)
- Use `[RelayCommand]` + `private void/async Task` method (don't create `ICommand` properties)
- Services injected via constructor, stored as `private readonly` fields
- **No business logic in ViewModel** — delegate to services
- **No direct SQLite access** from ViewModel
- Use `WeakReferenceMessenger.Default` for cross-VM communication
- ViewModel is `partial class` — source generator creates the rest

## Property Change Hooks

```csharp
[ObservableProperty]
public partial string Name { get; set; } = "";

// Source generator calls this when Name changes
partial void OnNameChanged(string value)
{
    // React to change here
}
```

## CanExecute for Commands

```csharp
[RelayCommand(CanExecute = nameof(CanSave))]
private async Task SaveAsync()
{
    // ...
}

private bool CanSave() => !string.IsNullOrWhiteSpace(Name);
// Source generator calls NotifyCanExecuteChanged automatically
```
