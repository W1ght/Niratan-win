using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;

namespace Niratan.ViewModels.Components;

public sealed partial class MangaBrowseSourceItemViewModel : ObservableObject
{
    private readonly Func<Task<string?>>? _loadIcon;
    private readonly Func<Task>? _remove;
    private int _iconLoadStarted;

    public MangaBrowseSourceItemViewModel(
        string name,
        string language,
        string provider,
        Func<Task> open,
        Func<Task<string?>>? loadIcon = null,
        Func<Task>? remove = null)
    {
        Name = name;
        Language = language;
        Provider = provider;
        _loadIcon = loadIcon;
        _remove = remove;
        OpenCommand = new AsyncRelayCommand(open);
        RemoveCommand = new AsyncRelayCommand(
            () => _remove?.Invoke() ?? Task.CompletedTask,
            () => CanRemove);
    }

    public string Name { get; }
    public string Language { get; }
    public string Provider { get; }
    public string LanguageLabel => GetLanguageLabel(Language);
    public string Metadata => string.IsNullOrWhiteSpace(LanguageLabel)
        ? Provider
        : $"{LanguageLabel} · {Provider}";
    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }
    public bool CanRemove => _remove is not null;
    public string RemoveActionLabel => ResourceStringHelper.GetString(
        "MihonRemoveSourceAction",
        "Remove");
    public string RemoveAutomationId =>
        $"BrowseSourceRemove_{SanitizeAutomationSegment(Name)}_"
        + $"{SanitizeAutomationSegment(Language)}_"
        + SanitizeAutomationSegment(Provider);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    [NotifyPropertyChangedFor(nameof(NeedsIconFallback))]
    public partial BitmapImage? IconImage { get; set; }

    public bool HasIcon => IconImage is not null;
    public bool NeedsIconFallback => !HasIcon;

    public async Task EnsureIconAsync()
    {
        if (_loadIcon is null
            || Interlocked.Exchange(ref _iconLoadStarted, 1) != 0)
        {
            return;
        }

        try
        {
            var path = await _loadIcon();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                // Let the Image control own the decode lifecycle. Explicitly
                // feeding a third-party stream to SetSourceAsync can make
                // WinUI.Xaml fail-fast on a malformed or unsupported icon.
                IconImage = new BitmapImage(
                    new Uri(Path.GetFullPath(path), UriKind.Absolute));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // A missing or invalid third-party icon must not hide the source.
        }
    }

    internal static string GetLanguageLabel(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)
            || string.Equals(
                language,
                "localsourcelang",
                StringComparison.OrdinalIgnoreCase))
        {
            return ResourceStringHelper.GetString(
                "BrowseOtherSourceGroup",
                "Other");
        }

        try
        {
            return CultureInfo.GetCultureInfo(language).DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return language.ToUpperInvariant();
        }
    }

    private static string SanitizeAutomationSegment(string? value) =>
        new((value ?? string.Empty)
            .Select(character =>
                char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
}

public sealed class MangaBrowseSourceGroupViewModel
    : ObservableCollection<MangaBrowseSourceItemViewModel>
{
    public MangaBrowseSourceGroupViewModel(
        string label,
        System.Collections.Generic.IEnumerable<MangaBrowseSourceItemViewModel> items)
        : base(items)
    {
        Label = label;
    }

    public string Label { get; }
}
