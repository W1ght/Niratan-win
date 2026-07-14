---
name: xaml
description: WinUI 3 XAML conventions — x:Uid i18n, layout, styles, data binding
---

# WinUI 3 XAML Conventions

## Page Structure

```xml
<Page
    x:Class="Niratan.Views.Pages.FooPage"
    xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
    xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
    xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
    mc:Ignorable="d">

    <Grid>
        <!-- Content -->
    </Grid>
</Page>
```

## Code-behind (minimal)

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

    public FooViewModel ViewModel => (FooViewModel)DataContext;
}
```

No business logic in code-behind.

## i18n with x:Uid

Every user-visible string must use `x:Uid`:

```xml
<TextBlock x:Uid="FooTitle" />
<Button x:Uid="FooSaveButton" Content="Save" />
```

Resources in:
- `Strings/en-US/Resources.resw`
- `Strings/zh-CN/Resources.resw`

Key convention: `FooTitle.Text`, `FooSaveButton.Content`

## Data Binding

```xml
<!-- One-way binding (default) -->
<TextBlock Text="{x:Bind ViewModel.BookTitle, Mode=OneWay}" />

<!-- Two-way binding -->
<TextBox Text="{x:Bind ViewModel.SearchQuery, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}" />

<!-- Command binding -->
<Button Command="{x:Bind ViewModel.SaveCommand}" />

<!-- Compiled binding (x:Bind) preferred over Binding -->
```

## Layout Rules

- Use `Grid` for primary layout, not `StackPanel` everywhere
- `RelativePanel` for responsive layouts
- Avoid fixed widths/heights — use `*`/`Auto` grid rows/columns
- Mica/Acrylic backdrops set in `MainWindow`, not individual pages

## Styles & Themes

- Theme-aware colors via `ThemeResource`, not hardcoded
- Dark/light support required for all UI
- Reader-specific styles in `NovelReaderContentStyles.cs`, not XAML

## AutomationId

Every interactive element for testing needs `AutomationId`:

```xml
<Button x:Uid="FooSave" AutomationId="FooSaveButton" />
<TextBox AutomationId="FooSearchBox" />
```
