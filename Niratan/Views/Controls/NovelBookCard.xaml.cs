using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Niratan.Views.Controls;

public sealed partial class NovelBookCard : UserControl
{
    public static readonly DependencyProperty AutomationIdProperty = DependencyProperty.Register(
        nameof(AutomationId),
        typeof(string),
        typeof(NovelBookCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(NovelBookCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CoverImageProperty = DependencyProperty.Register(
        nameof(CoverImage),
        typeof(ImageSource),
        typeof(NovelBookCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty HasCoverProperty = DependencyProperty.Register(
        nameof(HasCover),
        typeof(bool),
        typeof(NovelBookCard),
        new PropertyMetadata(false));

    public static readonly DependencyProperty OverallProgressPercentProperty = DependencyProperty.Register(
        nameof(OverallProgressPercent),
        typeof(double),
        typeof(NovelBookCard),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty OverallProgressTextProperty = DependencyProperty.Register(
        nameof(OverallProgressText),
        typeof(string),
        typeof(NovelBookCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PlaceholderGlyphProperty = DependencyProperty.Register(
        nameof(PlaceholderGlyph),
        typeof(string),
        typeof(NovelBookCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty PlaceholderOpacityProperty = DependencyProperty.Register(
        nameof(PlaceholderOpacity),
        typeof(double),
        typeof(NovelBookCard),
        new PropertyMetadata(0.3d));

    public static readonly DependencyProperty IsDownloadingProperty = DependencyProperty.Register(
        nameof(IsDownloading),
        typeof(bool),
        typeof(NovelBookCard),
        new PropertyMetadata(false));

    public static readonly DependencyProperty DownloadProgressProperty = DependencyProperty.Register(
        nameof(DownloadProgress),
        typeof(double),
        typeof(NovelBookCard),
        new PropertyMetadata(0d));

    public static readonly DependencyProperty HasDownloadStatusProperty = DependencyProperty.Register(
        nameof(HasDownloadStatus),
        typeof(bool),
        typeof(NovelBookCard),
        new PropertyMetadata(false));

    public static readonly DependencyProperty DownloadStatusTextProperty = DependencyProperty.Register(
        nameof(DownloadStatusText),
        typeof(string),
        typeof(NovelBookCard),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command),
        typeof(ICommand),
        typeof(NovelBookCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter),
        typeof(object),
        typeof(NovelBookCard),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CardContextFlyoutProperty = DependencyProperty.Register(
        nameof(CardContextFlyout),
        typeof(FlyoutBase),
        typeof(NovelBookCard),
        new PropertyMetadata(null));

    public NovelBookCard()
    {
        InitializeComponent();
    }

    public string AutomationId
    {
        get => (string)GetValue(AutomationIdProperty);
        set => SetValue(AutomationIdProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public ImageSource? CoverImage
    {
        get => (ImageSource?)GetValue(CoverImageProperty);
        set => SetValue(CoverImageProperty, value);
    }

    public bool HasCover
    {
        get => (bool)GetValue(HasCoverProperty);
        set => SetValue(HasCoverProperty, value);
    }

    public double OverallProgressPercent
    {
        get => (double)GetValue(OverallProgressPercentProperty);
        set => SetValue(OverallProgressPercentProperty, value);
    }

    public string OverallProgressText
    {
        get => (string)GetValue(OverallProgressTextProperty);
        set => SetValue(OverallProgressTextProperty, value);
    }

    public string PlaceholderGlyph
    {
        get => (string)GetValue(PlaceholderGlyphProperty);
        set => SetValue(PlaceholderGlyphProperty, value);
    }

    public double PlaceholderOpacity
    {
        get => (double)GetValue(PlaceholderOpacityProperty);
        set => SetValue(PlaceholderOpacityProperty, value);
    }

    public bool IsDownloading
    {
        get => (bool)GetValue(IsDownloadingProperty);
        set => SetValue(IsDownloadingProperty, value);
    }

    public double DownloadProgress
    {
        get => (double)GetValue(DownloadProgressProperty);
        set => SetValue(DownloadProgressProperty, value);
    }

    public bool HasDownloadStatus
    {
        get => (bool)GetValue(HasDownloadStatusProperty);
        set => SetValue(HasDownloadStatusProperty, value);
    }

    public string DownloadStatusText
    {
        get => (string)GetValue(DownloadStatusTextProperty);
        set => SetValue(DownloadStatusTextProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    public FlyoutBase? CardContextFlyout
    {
        get => (FlyoutBase?)GetValue(CardContextFlyoutProperty);
        set => SetValue(CardContextFlyoutProperty, value);
    }
}
