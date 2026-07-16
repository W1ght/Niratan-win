using System;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Niratan.Models.Anki;

namespace Niratan.Views.Dialogs;

public sealed class MiningContextSelectionDialog : ContentDialog
{
    private readonly MiningContextSelection _selection;
    private readonly int _targetLength;
    private readonly Func<MiningContextSelectionRange, Task<AnkiMiningResult>> _confirmAsync;
    private readonly TextBlock _summary = new();
    private readonly StackPanel _sentences = new() { Spacing = -8 };
    private readonly Button _removePreviousButton;
    private readonly Button _addPreviousButton;
    private readonly Button _removeNextButton;
    private readonly Button _addNextButton;
    private readonly InfoBar _resultInfoBar = new() { IsOpen = false, IsClosable = false };
    private int _lowerBound;
    private int _upperBound;
    private bool _isSubmitting;

    public MiningContextSelectionDialog(
        MiningContextSelection selection,
        int targetLength,
        Func<MiningContextSelectionRange, Task<AnkiMiningResult>> confirmAsync)
    {
        _selection = selection;
        _targetLength = Math.Max(0, targetLength);
        _confirmAsync = confirmAsync;
        _lowerBound = selection.CurrentIndex;
        _upperBound = selection.CurrentIndex;

        Title = "Select Sentence Context";
        PrimaryButtonText = "Confirm Mining";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;
        Resources["ContentDialogMaxWidth"] = 680d;
        AutomationProperties.SetAutomationId(this, "MiningContextSelectionDialog");

        _removePreviousButton = CreateRangeButton("Remove Previous", "−", RemovePrevious);
        _addPreviousButton = CreateRangeButton("Add Previous", "+", AddPrevious);
        _removeNextButton = CreateRangeButton("Remove Next", "−", RemoveNext);
        _addNextButton = CreateRangeButton("Add Next", "+", AddNext);

        var controls = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto },
            },
            ColumnSpacing = 8,
        };
        controls.Children.Add(_removePreviousButton);
        controls.Children.Add(_addPreviousButton);
        controls.Children.Add(_removeNextButton);
        controls.Children.Add(_addNextButton);
        Grid.SetColumn(_removePreviousButton, 0);
        Grid.SetColumn(_addPreviousButton, 1);
        Grid.SetColumn(_removeNextButton, 3);
        Grid.SetColumn(_addNextButton, 4);

        var scrollViewer = new ScrollViewer
        {
            Content = _sentences,
            MaxHeight = 360,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        Content = new StackPanel
        {
            MinWidth = 420,
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = "Adjust before mining",
                    FontSize = 12,
                    Opacity = 0.72,
                },
                _summary,
                scrollViewer,
                controls,
                _resultInfoBar,
            },
        };

        PrimaryButtonClick += OnPrimaryButtonClick;
        Refresh();
    }

    private static Button CreateRangeButton(string label, string glyph, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = glyph, FontSize = 16 },
                    new TextBlock { Text = label },
                },
            },
        };
        button.Click += handler;
        AutomationProperties.SetName(button, label);
        return button;
    }

    private void AddPrevious(object sender, RoutedEventArgs e)
    {
        if (_lowerBound > 0)
        {
            _lowerBound--;
            Refresh();
        }
    }

    private void RemovePrevious(object sender, RoutedEventArgs e)
    {
        if (_lowerBound < _selection.CurrentIndex)
        {
            _lowerBound++;
            Refresh();
        }
    }

    private void AddNext(object sender, RoutedEventArgs e)
    {
        if (_upperBound + 1 < _selection.Sentences.Count)
        {
            _upperBound++;
            Refresh();
        }
    }

    private void RemoveNext(object sender, RoutedEventArgs e)
    {
        if (_upperBound > _selection.CurrentIndex)
        {
            _upperBound--;
            Refresh();
        }
    }

    private void Refresh()
    {
        _summary.Text = $"Selected {_upperBound - _lowerBound + 1} sentences";
        _summary.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _sentences.Children.Clear();

        for (var index = _lowerBound; index <= _upperBound; index++)
        {
            var sentence = _selection.Sentences[index];
            var isCurrent = index == _selection.CurrentIndex;
            var label = isCurrent
                ? "Current Sentence"
                : index < _selection.CurrentIndex ? "Previous Context" : "Next Context";
            var text = CreateSentenceText(sentence, isCurrent);
            var panel = new StackPanel
            {
                Spacing = 7,
                Children =
                {
                    new TextBlock { Text = label, Opacity = 0.68, FontSize = 12 },
                    text,
                },
            };
            var border = new Border
            {
                Padding = new Thickness(isCurrent ? 18 : 15),
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(isCurrent ? Colors.DodgerBlue : Colors.Gray) { Opacity = isCurrent ? 0.58 : 0.25 },
                Background = new SolidColorBrush(isCurrent ? Colors.DodgerBlue : Colors.Gray) { Opacity = isCurrent ? 0.14 : 0.08 },
                Opacity = isCurrent ? 1 : 0.8,
                Child = panel,
            };
            _sentences.Children.Add(border);
        }

        _removePreviousButton.IsEnabled = !_isSubmitting && _lowerBound < _selection.CurrentIndex;
        _addPreviousButton.IsEnabled = !_isSubmitting && _lowerBound > 0;
        _removeNextButton.IsEnabled = !_isSubmitting && _upperBound > _selection.CurrentIndex;
        _addNextButton.IsEnabled = !_isSubmitting && _upperBound + 1 < _selection.Sentences.Count;
        IsPrimaryButtonEnabled = !_isSubmitting;
        IsSecondaryButtonEnabled = !_isSubmitting;
    }

    private TextBlock CreateSentenceText(MiningContextSentence sentence, bool isCurrent)
    {
        var textBlock = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontWeight = isCurrent
                ? Microsoft.UI.Text.FontWeights.SemiBold
                : Microsoft.UI.Text.FontWeights.Normal,
        };
        if (!isCurrent
            || sentence.TargetUtf16Location is not int location
            || _targetLength <= 0
            || location < 0
            || location >= sentence.Text.Length)
        {
            textBlock.Text = sentence.Text;
            return textBlock;
        }

        var length = Math.Min(_targetLength, sentence.Text.Length - location);
        textBlock.Inlines.Add(new Run { Text = sentence.Text[..location] });
        textBlock.Inlines.Add(new Run
        {
            Text = sentence.Text.Substring(location, length),
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = new SolidColorBrush(Colors.DodgerBlue),
        });
        textBlock.Inlines.Add(new Run { Text = sentence.Text[(location + length)..] });
        return textBlock;
    }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_isSubmitting)
        {
            args.Cancel = true;
            return;
        }

        args.Cancel = true;
        var deferral = args.GetDeferral();
        _isSubmitting = true;
        _resultInfoBar.IsOpen = false;
        Refresh();
        try
        {
            var result = await _confirmAsync(new MiningContextSelectionRange(_lowerBound, _upperBound));
            if (result.Status is AnkiMiningStatus.Added or AnkiMiningStatus.Pending)
            {
                args.Cancel = false;
                return;
            }

            _resultInfoBar.Title = result.Status == AnkiMiningStatus.Duplicate
                ? "Duplicate Found"
                : "Add Failed";
            _resultInfoBar.Message = result.Message;
            _resultInfoBar.Severity = result.Status == AnkiMiningStatus.Duplicate
                ? InfoBarSeverity.Warning
                : InfoBarSeverity.Error;
            _resultInfoBar.IsOpen = true;
        }
        finally
        {
            _isSubmitting = false;
            Refresh();
            deferral.Complete();
        }
    }
}
