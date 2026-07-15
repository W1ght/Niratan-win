using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Niratan.Views.Controls;

public sealed partial class CompactColorPicker : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color),
        typeof(Color),
        typeof(CompactColorPicker),
        new PropertyMetadata(Colors.Transparent, OnColorPropertyChanged));

    public static readonly DependencyProperty IsAlphaEnabledProperty = DependencyProperty.Register(
        nameof(IsAlphaEnabled),
        typeof(bool),
        typeof(CompactColorPicker),
        new PropertyMetadata(false, OnIsAlphaEnabledPropertyChanged));

    public static readonly DependencyProperty FlyoutPlacementProperty = DependencyProperty.Register(
        nameof(FlyoutPlacement),
        typeof(FlyoutPlacementMode),
        typeof(CompactColorPicker),
        new PropertyMetadata(FlyoutPlacementMode.Auto));

    private bool _isSynchronizingPicker;
    private bool _isAutomationConfigured;

    public CompactColorPicker()
    {
        InitializeComponent();
        Loaded += CompactColorPicker_Loaded;
    }

    public event TypedEventHandler<CompactColorPicker, ColorChangedEventArgs>? ColorChanged;

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public bool IsAlphaEnabled
    {
        get => (bool)GetValue(IsAlphaEnabledProperty);
        set => SetValue(IsAlphaEnabledProperty, value);
    }

    public FlyoutPlacementMode FlyoutPlacement
    {
        get => (FlyoutPlacementMode)GetValue(FlyoutPlacementProperty);
        set => SetValue(FlyoutPlacementProperty, value);
    }

    public SolidColorBrush SwatchBrush { get; } = new(Colors.Transparent);

    private static void OnColorPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (CompactColorPicker)dependencyObject;
        var color = (Color)args.NewValue;
        control.SwatchBrush.Color = color;

        if (control.Picker.Color.Equals(color))
            return;

        control._isSynchronizingPicker = true;
        control.Picker.Color = color;
        control._isSynchronizingPicker = false;
    }

    private static void OnIsAlphaEnabledPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        var control = (CompactColorPicker)dependencyObject;
        control.Picker.IsAlphaEnabled = (bool)args.NewValue;
    }

    private void Picker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isSynchronizingPicker)
            return;

        Color = args.NewColor;
        ColorChanged?.Invoke(this, args);
    }

    private void CompactColorPicker_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isAutomationConfigured)
            return;

        _isAutomationConfigured = true;
        var automationId = AutomationProperties.GetAutomationId(this);
        if (string.IsNullOrWhiteSpace(automationId))
            return;

        AutomationProperties.SetAutomationId(SwatchButton, automationId);
        AutomationProperties.SetAutomationId(Picker, $"{automationId}Flyout");
        AutomationProperties.SetAutomationId(this, string.Empty);
    }
}
