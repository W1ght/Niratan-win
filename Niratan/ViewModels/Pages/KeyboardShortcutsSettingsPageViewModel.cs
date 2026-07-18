using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Niratan.Helpers;
using Niratan.Models.Shortcuts;
using Niratan.Services.Shortcuts;

namespace Niratan.ViewModels.Pages;

public partial class KeyboardShortcutsSettingsPageViewModel : ObservableObject
{
    private readonly IShortcutService _shortcutService;

    public ObservableCollection<ShortcutSectionViewModel> Sections { get; } = [];
    public ObservableCollection<ShortcutRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRecording))]
    [NotifyPropertyChangedFor(nameof(RecordingHintText))]
    public partial ShortcutRowViewModel? RecordingRow { get; set; }

    public bool IsRecording => RecordingRow != null;

    public string RecordingHintText =>
        RecordingRow == null
            ? ""
            : ResourceStringHelper.FormatString(
                "KeyboardShortcutsRecordingHint",
                "Press a shortcut for {0}. Esc cancels.",
                RecordingRow.Title);

    public KeyboardShortcutsSettingsPageViewModel(IShortcutService shortcutService)
    {
        _shortcutService = shortcutService;
        BuildRows();
        _shortcutService.ShortcutsChanged += (_, _) => RefreshRows();
    }

    public void StartRecording(ShortcutRowViewModel row)
    {
        if (RecordingRow != null)
            RecordingRow.IsRecording = false;

        RecordingRow = row;
        row.IsRecording = true;
    }

    public void CancelRecording()
    {
        if (RecordingRow != null)
            RecordingRow.IsRecording = false;

        RecordingRow = null;
    }

    public void CaptureShortcut(KeyboardShortcutBinding binding)
    {
        if (RecordingRow == null || binding.IsEmpty)
            return;

        var row = RecordingRow;
        row.IsRecording = false;
        RecordingRow = null;
        _shortcutService.SetBinding(row.Action, binding);
        RefreshRows();
    }

    public void ResetShortcut(ShortcutRowViewModel row)
    {
        _shortcutService.ResetBinding(row.Action);
        RefreshRows();
    }

    private void BuildRows()
    {
        Sections.Clear();
        Rows.Clear();
        foreach (var category in ShortcutCategoryOrder.All)
        {
            var sectionRows = _shortcutService.Registry.ActionsIn(category)
                .Select(action =>
                {
                    var row = new ShortcutRowViewModel(action);
                    UpdateRow(row);
                    Rows.Add(row);
                    return row;
                })
                .ToList();

            if (sectionRows.Count > 0)
                Sections.Add(new ShortcutSectionViewModel(category, sectionRows));
        }
    }

    private void RefreshRows()
    {
        foreach (var row in Rows)
            UpdateRow(row);
    }

    private void UpdateRow(ShortcutRowViewModel row)
    {
        var binding = _shortcutService.GetBinding(row.Action);
        var conflicts = _shortcutService.GetConflicts(row.Action, binding);
        row.Update(binding, conflicts);
    }
}

public sealed class ShortcutSectionViewModel
{
    public ShortcutSectionViewModel(
        ShortcutCategory category,
        IReadOnlyList<ShortcutRowViewModel> rows)
    {
        Category = category;
        Title = ShortcutCategoryTitle.For(category);
        Rows = rows;
    }

    public ShortcutCategory Category { get; }
    public string Title { get; }
    public IReadOnlyList<ShortcutRowViewModel> Rows { get; }
}

public partial class ShortcutRowViewModel : ObservableObject
{
    public ShortcutRowViewModel(ShortcutAction action)
    {
        Action = action;
        Title = ResourceStringHelper.GetString(action.TitleResourceKey, action.Title);
        DefaultShortcutText = ResourceStringHelper.FormatString(
            "KeyboardShortcutsDefaultText",
            "Default Shortcut: {0}",
            action.DefaultBinding.Label);
    }

    public ShortcutAction Action { get; }
    public string Title { get; }
    public string DefaultShortcutText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutButtonLabel))]
    public partial string CurrentShortcutLabel { get; set; } = "";

    [ObservableProperty]
    public partial string ConflictText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasConflict { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShortcutButtonLabel))]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReset))]
    public partial bool IsDefault { get; set; }

    public bool CanReset => !IsDefault;

    public string ShortcutButtonLabel =>
        IsRecording
            ? ResourceStringHelper.GetString("KeyboardShortcutsPressKeysText", "Press keys...")
            : CurrentShortcutLabel;

    public void Update(KeyboardShortcutBinding binding, IReadOnlyList<ShortcutConflict> conflicts)
    {
        CurrentShortcutLabel = string.IsNullOrWhiteSpace(binding.Label)
            ? ResourceStringHelper.GetString("KeyboardShortcutsUnassignedText", "Unassigned")
            : binding.Label;
        IsDefault = binding.Matches(Action.DefaultBinding);
        HasConflict = conflicts.Any(conflict => conflict.Kind == ShortcutConflictKind.Conflict);
        ConflictText = FormatConflict(conflicts);
    }

    private static string FormatConflict(IReadOnlyList<ShortcutConflict> conflicts)
    {
        var conflict = conflicts.FirstOrDefault(item => item.Kind == ShortcutConflictKind.Conflict);
        if (conflict != null)
        {
            return ResourceStringHelper.FormatString(
                "KeyboardShortcutsConflictText",
                "Conflicts with {0}",
                ResourceStringHelper.GetString(conflict.Action.TitleResourceKey, conflict.Action.Title));
        }

        var shadowed = conflicts.FirstOrDefault(item => item.Kind == ShortcutConflictKind.Shadowed);
        if (shadowed != null)
        {
            return ResourceStringHelper.FormatString(
                "KeyboardShortcutsShadowedText",
                "Shadowed by {0}",
                ResourceStringHelper.GetString(shadowed.Action.TitleResourceKey, shadowed.Action.Title));
        }

        return "";
    }
}

internal static class ShortcutCategoryOrder
{
    public static IReadOnlyList<ShortcutCategory> All { get; } =
    [
        ShortcutCategory.Global,
        ShortcutCategory.Reader,
        ShortcutCategory.DictionaryPopup,
        ShortcutCategory.Sasayaki,
        ShortcutCategory.Video,
    ];
}

internal static class ShortcutCategoryTitle
{
    public static string For(ShortcutCategory category) =>
        category switch
        {
            ShortcutCategory.Global => ResourceStringHelper.GetString("ShortcutCategoryGlobal", "Global"),
            ShortcutCategory.Reader => ResourceStringHelper.GetString("ShortcutCategoryReader", "Reader"),
            ShortcutCategory.DictionaryPopup => ResourceStringHelper.GetString(
                "ShortcutCategoryDictionaryPopup",
                "Dictionary / Popup"),
            ShortcutCategory.Sasayaki => ResourceStringHelper.GetString("ShortcutCategorySasayaki", "Sasayaki"),
            ShortcutCategory.Video => ResourceStringHelper.GetString("ShortcutCategoryVideo", "Video"),
            _ => category.ToString(),
        };
}
