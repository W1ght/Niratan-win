using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Novel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Niratan.Views.Controls;

public sealed partial class NovelStatisticsTrendChart : UserControl
{
    private const double MinimumPointHeight = 3;
    private const double LeftGutter = 88;
    private const double RightGutter = 8;
    private const double TopGutter = 8;
    private const double BottomGutter = 28;

    public static readonly DependencyProperty ItemsSourceProperty =
        DependencyProperty.Register(
            nameof(ItemsSource),
            typeof(IEnumerable),
            typeof(NovelStatisticsTrendChart),
            new PropertyMetadata(null, OnChartPropertyChanged));

    public static readonly DependencyProperty ChartStyleProperty =
        DependencyProperty.Register(
            nameof(ChartStyle),
            typeof(NovelStatisticsTrendChartStyle),
            typeof(NovelStatisticsTrendChart),
            new PropertyMetadata(NovelStatisticsTrendChartStyle.Bar, OnChartPropertyChanged));

    public static readonly DependencyProperty AxisTicksProperty =
        DependencyProperty.Register(
            nameof(AxisTicks),
            typeof(IEnumerable),
            typeof(NovelStatisticsTrendChart),
            new PropertyMetadata(null, OnChartPropertyChanged));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public NovelStatisticsTrendChartStyle ChartStyle
    {
        get => (NovelStatisticsTrendChartStyle)GetValue(ChartStyleProperty);
        set => SetValue(ChartStyleProperty, value);
    }

    public IEnumerable? AxisTicks
    {
        get => (IEnumerable?)GetValue(AxisTicksProperty);
        set => SetValue(AxisTicksProperty, value);
    }

    public NovelStatisticsTrendChart()
    {
        InitializeComponent();
        Loaded += (_, _) => RenderChart();
        SizeChanged += (_, _) => RenderChart();
    }

    private static void OnChartPropertyChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is NovelStatisticsTrendChart chart)
            chart.RenderChart();
    }

    private void RenderChart()
    {
        if (ChartCanvas == null)
            return;

        ChartCanvas.Children.Clear();
        var points = ItemsSource?
            .Cast<object>()
            .OfType<NovelStatisticsTrendDisplayPoint>()
            .ToList() ?? [];
        var axisTicks = AxisTicks?
            .Cast<object>()
            .OfType<NovelStatisticsAxisTickDisplay>()
            .ToList() ?? [];
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        var plot = new Rect(
            LeftGutter,
            TopGutter,
            Math.Max(width - LeftGutter - RightGutter, 0),
            Math.Max(height - TopGutter - BottomGutter, 0));
        if (plot.Width <= 0 || plot.Height <= 0)
            return;

        var accent = AccentBrushSource.Background
            ?? new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        var gridBrush = GridBrushSource.Background
            ?? new SolidColorBrush(Color.FromArgb(48, 128, 128, 128));
        var labelBrush = AxisLabelBrushSource.Background
            ?? new SolidColorBrush(Color.FromArgb(190, 128, 128, 128));
        DrawYAxisTicks(plot, axisTicks, gridBrush, labelBrush);
        DrawXAxisLabels(points, plot, labelBrush);

        if (points.Count == 0)
            return;

        if (ChartStyle == NovelStatisticsTrendChartStyle.Line)
            DrawLine(points, plot, accent);
        else
            DrawBars(points, plot, accent);
    }

    private void DrawYAxisTicks(
        Rect plot,
        IReadOnlyList<NovelStatisticsAxisTickDisplay> ticks,
        Brush gridBrush,
        Brush labelBrush)
    {
        foreach (var tick in ticks)
        {
            var normalized = Math.Clamp(tick.NormalizedValue, 0, 1);
            var y = plot.Top + (1 - normalized) * plot.Height;
            ChartCanvas.Children.Add(new Line
            {
                X1 = plot.Left,
                X2 = plot.Right,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = normalized <= 0 ? 1.5 : 1,
            });

            var label = new TextBlock
            {
                Width = LeftGutter - 8,
                FontSize = 11,
                Foreground = labelBrush,
                Text = tick.Label,
                TextAlignment = TextAlignment.Right,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(label, 0);
            Canvas.SetTop(label, Math.Max(y - 8, 0));
            ChartCanvas.Children.Add(label);
        }

        ChartCanvas.Children.Add(new Line
        {
            X1 = plot.Left,
            X2 = plot.Left,
            Y1 = plot.Top,
            Y2 = plot.Bottom,
            Stroke = gridBrush,
            StrokeThickness = 1,
        });
    }

    private void DrawXAxisLabels(
        IReadOnlyList<NovelStatisticsTrendDisplayPoint> points,
        Rect plot,
        Brush labelBrush)
    {
        if (points.Count == 0)
            return;

        var indices = new[] { 0, points.Count / 2, points.Count - 1 }
            .Distinct()
            .ToList();
        var denominator = Math.Max(points.Count - 1, 1);
        foreach (var index in indices)
        {
            var x = points.Count == 1
                ? plot.Left + plot.Width / 2
                : plot.Left + plot.Width * index / denominator;
            var labelWidth = Math.Min(80, plot.Width);
            var label = new TextBlock
            {
                Width = labelWidth,
                FontSize = 11,
                Foreground = labelBrush,
                Text = points[index].Label,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Canvas.SetLeft(
                label,
                Math.Clamp(
                    x - labelWidth / 2,
                    plot.Left,
                    plot.Right - labelWidth));
            Canvas.SetTop(label, plot.Bottom + 4);
            ChartCanvas.Children.Add(label);
        }
    }

    private void DrawBars(
        IReadOnlyList<NovelStatisticsTrendDisplayPoint> points,
        Rect plot,
        Brush accent)
    {
        var slotWidth = plot.Width / points.Count;
        var barWidth = Math.Clamp(slotWidth * 0.62, 2, 28);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var valueHeight = point.NormalizedValue <= 0
                ? 0
                : Math.Max(
                    Math.Clamp(point.NormalizedValue, 0, 1) * plot.Height,
                    MinimumPointHeight);
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = valueHeight,
                Fill = accent,
                RadiusX = Math.Min(3, barWidth / 2),
                RadiusY = Math.Min(3, barWidth / 2),
            };
            SetPointMetadata(bar, point);
            Canvas.SetLeft(
                bar,
                plot.Left + index * slotWidth + (slotWidth - barWidth) / 2);
            Canvas.SetTop(bar, plot.Bottom - valueHeight);
            ChartCanvas.Children.Add(bar);
        }
    }

    private void DrawLine(
        IReadOnlyList<NovelStatisticsTrendDisplayPoint> points,
        Rect plot,
        Brush accent)
    {
        var line = new Polyline
        {
            Stroke = accent,
            StrokeThickness = 2.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var denominator = Math.Max(points.Count - 1, 1);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var x = plot.Left + plot.Width * index / denominator;
            var y = plot.Bottom
                - Math.Clamp(point.NormalizedValue, 0, 1) * plot.Height;
            line.Points.Add(new Point(x, y));
        }
        ChartCanvas.Children.Add(line);

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var marker = new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = accent,
            };
            SetPointMetadata(marker, point);
            Canvas.SetLeft(
                marker,
                plot.Left + plot.Width * index / denominator - 3.5);
            Canvas.SetTop(
                marker,
                plot.Bottom
                    - Math.Clamp(point.NormalizedValue, 0, 1) * plot.Height
                    - 3.5);
            ChartCanvas.Children.Add(marker);
        }
    }

    private static void SetPointMetadata(
        FrameworkElement element,
        NovelStatisticsTrendDisplayPoint point)
    {
        ToolTipService.SetToolTip(element, point.ToolTipText);
        AutomationProperties.SetName(element, $"{point.Label}, {point.ValueText}");
    }
}
