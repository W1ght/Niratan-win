using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Hoshi.Models.Novel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Hoshi.Views.Controls;

public sealed partial class NovelStatisticsTrendChart : UserControl
{
    private const double MinimumPointHeight = 3;

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
        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (points.Count == 0 || width <= 0 || height <= 0)
            return;

        var accent = AccentBrushSource.Background
            ?? new SolidColorBrush(Color.FromArgb(255, 0, 120, 212));
        var gridBrush = GridBrushSource.Background
            ?? new SolidColorBrush(Color.FromArgb(48, 128, 128, 128));
        DrawGrid(width, height, gridBrush);

        if (ChartStyle == NovelStatisticsTrendChartStyle.Line)
            DrawLine(points, width, height, accent);
        else
            DrawBars(points, width, height, accent);
    }

    private void DrawGrid(double width, double height, Brush brush)
    {
        for (var index = 0; index <= 4; index++)
        {
            var y = height * index / 4;
            ChartCanvas.Children.Add(new Line
            {
                X1 = 0,
                X2 = width,
                Y1 = y,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = index == 4 ? 1.5 : 1,
            });
        }
    }

    private void DrawBars(
        IReadOnlyList<NovelStatisticsTrendDisplayPoint> points,
        double width,
        double height,
        Brush accent)
    {
        var slotWidth = width / points.Count;
        var barWidth = Math.Clamp(slotWidth * 0.62, 2, 28);
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var valueHeight = point.NormalizedValue <= 0
                ? 0
                : Math.Max(point.NormalizedValue * height, MinimumPointHeight);
            var bar = new Rectangle
            {
                Width = barWidth,
                Height = valueHeight,
                Fill = accent,
                RadiusX = Math.Min(3, barWidth / 2),
                RadiusY = Math.Min(3, barWidth / 2),
            };
            SetPointMetadata(bar, point);
            Canvas.SetLeft(bar, index * slotWidth + (slotWidth - barWidth) / 2);
            Canvas.SetTop(bar, height - valueHeight);
            ChartCanvas.Children.Add(bar);
        }
    }

    private void DrawLine(
        IReadOnlyList<NovelStatisticsTrendDisplayPoint> points,
        double width,
        double height,
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
            var x = width * index / denominator;
            var y = height - point.NormalizedValue * height;
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
            Canvas.SetLeft(marker, width * index / denominator - 3.5);
            Canvas.SetTop(marker, height - point.NormalizedValue * height - 3.5);
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
