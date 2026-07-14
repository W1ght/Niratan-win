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
        Rows.Clear();
        foreach (var action in _shortcutService.Registry.Actions)
        {
            var row = new ShortcutRowViewModel(action);
            UpdateRow(row);
            Rows.Add(row);
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

public partial class ShortcutRowViewModel : ObservableObject
{
    public ShortcutRowViewModel(ShortcutAction action)
    {
        Action = action;
        Title = ResourceStringHelper.GetString(action.TitleResourceKey, action.Title);
        CategoryTitle = action.Category switch
        {
            ShortcutCategory.Global => ResourceStringHelper.GetString("ShortcutCategoryGlobal", "Global"),
            ShortcutCategory.Reader => ResourceStringHelper.GetString("ShortcutCategoryReader", "Reader"),
            ShortcutCategory.DictionaryPopup => ResourceStringHelper.GetString(
                "ShortcutCategoryDictionaryPopup",
                "Dictionary Popup"),
            ShortcutCategory.Sasayaki => ResourceStringHelper.GetString("ShortcutCategorySasayaki", "Sasayaki"),
            ShortcutCategory.Video => ResourceStringHelper.GetString("ShortcutCategoryVideo", "Video"),
            _ => action.Category.ToString(),
        };
        DefaultShortcutLabel = action.DefaultBinding.Label;
    }

    public ShortcutAction Action { get; }
    public string Title { get; }
    public string CategoryTitle { get; }
    public string DefaultShortcutLabel { get; }

    [ObservableProperty]
    public partial string CurrentShortcutLabel { get; set; } = "";

    [ObservableProperty]
    public partial string ConflictText { get; set; } = "";

    [ObservableProperty]
    public partial bool HasConflict { get; set; }

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    public void Update(KeyboardShortcutBinding binding, IReadOnlyList<ShortcutConflict> conflicts)
    {
        CurrentShortcutLabel = string.IsNullOrWhiteSpace(binding.Label)
            ? ResourceStringHelper.GetString("KeyboardShortcutsUnassignedText", "Unassigned")
            : binding.Label;
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
