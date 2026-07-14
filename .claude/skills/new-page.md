---
name: new-page
description: Scaffold a new WinUI 3 Page + ViewModel + navigation + i18n resources
---

# New Page Scaffold

Create a new page in Niratan following project conventions.

## Files to Create

| File | Purpose |
|---|---|
| `Niratan/ViewModels/Pages/FooPageViewModel.cs` | ViewModel |
| `Niratan/Views/Pages/FooPage.xaml` | XAML UI |
| `Niratan/Views/Pages/FooPage.xaml.cs` | Code-behind |
| Update `Strings/en-US/Resources.resw` | English strings |
| Update `Strings/zh-CN/Resources.resw` | Chinese strings |
| Update `App.xaml.cs` | DI registration |
| Update `AppPage.cs` enum | Only if navigable |

## Step 1: ViewModel

`Niratan/ViewModels/Pages/FooPageViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Niratan.ViewModels.Pages;

public partial class FooPageViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "Foo";

    [RelayCommand]
    private void GoBack()
    {
        // navigation logic via INavigationService
    }
}
```

## Step 2: XAML Page

`Niratan/Views/Pages/FooPage.xaml`:

```xml
<Page
    x:Class="Niratan.Views.Pages.FooPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <Grid>
        <TextBlock x:Uid="FooTitle" Text="Foo" />
    </Grid>
</Page>
```

## Step 3: Code-behind

`Niratan/Views/Pages/FooPage.xaml.cs`:

```csharp
using Microsoft.UI.Xaml.Controls;
using Niratan.ViewModels.Pages;

namespace Niratan.Views.Pages;

public sealed partial class FooPage : Page
{
    public FooPage()
    {
        InitializeComponent();
    }

    public FooPageViewModel ViewModel => (FooPageViewModel)DataContext;
}
```

## Step 4: i18n Resources

Add keys to `Strings/en-US/Resources.resw` and `Strings/zh-CN/Resources.resw`:

```
FooTitle.Text → "Foo" / "Foo中文"
```

## Step 5: DI Registration

In `App.xaml.cs`, add to `ServiceCollection`:

```csharp
services.AddTransient<FooPageViewModel>();
```

## Step 6: Navigation (if needed)

- Add enum value to `Niratan/Enums/AppPage.cs`
- Add `case typeof(FooPage) => AppPage.Foo` in `NavigationService.CurrentPage`
- Navigate: `_navigationService.Navigate(typeof(FooPage))`
