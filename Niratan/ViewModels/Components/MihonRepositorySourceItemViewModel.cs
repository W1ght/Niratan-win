using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Niratan.Helpers;
using Niratan.Models.Manga;

namespace Niratan.ViewModels.Components;

public sealed class MihonLanguageFilterOption
{
    public MihonLanguageFilterOption(string code, string label)
    {
        Code = code;
        Label = label;
    }

    public string Code { get; }
    public string Label { get; }
}

public sealed partial class MihonRepositorySourceItemViewModel : ObservableObject
{
    private readonly Func<MihonExtensionSource, Task<string?>>? _loadIcon;
    private int _iconLoadStarted;

    public MihonRepositorySourceItemViewModel(
        MihonExtensionSource source,
        Func<MihonExtensionSource, Task> install,
        Func<MihonExtensionSource, Task<string?>>? loadIcon = null,
        Func<MihonExtensionSource, Task>? remove = null)
    {
        Source = source;
        _loadIcon = loadIcon;
        InstallCommand = new AsyncRelayCommand(() => install(Source));
        RemoveCommand = new AsyncRelayCommand(
            () => remove?.Invoke(Source) ?? Task.CompletedTask,
            () => CanRemove);
    }

    public MihonExtensionSource Source { get; }
    public string Name => Source.Name;
    public string Language => Source.Lang;
    public string LanguageLabel =>
        MangaBrowseSourceItemViewModel.GetLanguageLabel(Source.Lang);
    public string PackageName => Source.PackageName;
    public bool IsInstalled => Source.IsInstalled;
    public bool CanRemove => Source.IsInstalled;
    public bool IsNsfw => Source.IsNsfw;
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand RemoveCommand { get; }

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
            var path = await _loadIcon(Source);
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
            // Keep the puzzle fallback when a repository or APK has no icon.
        }
    }

    public string Metadata
    {
        get
        {
            var values = new[]
            {
                string.IsNullOrWhiteSpace(Source.Lang)
                    ? null
                    : LanguageLabel,
                Source.Version,
                Source.PackageDisplayName,
                Source.RepositoryName,
            };
            return string.Join(
                " · ",
                values.Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    public string InstallActionLabel =>
        Source.IsInstalled
            ? ResourceStringHelper.GetString(
                "MihonUpdateSourceAction",
                "Update")
            : ResourceStringHelper.GetString(
                "MihonInstallSourceAction",
                "Install");

    public string InstalledBadgeLabel => ResourceStringHelper.GetString(
        "MihonInstalledSourceBadge",
        "Installed");

    public string RemoveActionLabel => ResourceStringHelper.GetString(
        "MihonRemoveSourceAction",
        "Remove");

    public string NsfwBadgeLabel => ResourceStringHelper.GetString(
        "MihonNsfwSourceBadge",
        "NSFW");

    public string AutomationId =>
        $"MihonRepositorySource_{SanitizeAutomationSegment(Source.PackageName)}_{SanitizeAutomationSegment(Source.Id)}";

    public string RemoveAutomationId => $"{AutomationId}_Remove";

    public bool Matches(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var value = query.Trim();
        return Contains(Source.Name, value)
               || Contains(Source.PackageDisplayName, value)
               || Contains(Source.PackageName, value)
               || Contains(Source.Lang, value)
               || Contains(Source.BaseUrl, value)
               || Contains(Source.RepositoryName, value);
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Contains(query, StringComparison.CurrentCultureIgnoreCase);

    private static string SanitizeAutomationSegment(string value) =>
        new(value.Select(character =>
                char.IsLetterOrDigit(character) ? character : '_')
            .ToArray());
}

public sealed class MihonRepositorySourceGroup
    : ObservableCollection<MihonRepositorySourceItemViewModel>
{
    public MihonRepositorySourceGroup(
        string label,
        IEnumerable<MihonRepositorySourceItemViewModel> items)
        : base(items)
    {
        Label = label;
    }

    public string Label { get; }
}
