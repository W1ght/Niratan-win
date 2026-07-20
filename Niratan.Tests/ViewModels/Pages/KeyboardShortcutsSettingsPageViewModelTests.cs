using FluentAssertions;
using Niratan.Models.Shortcuts;
using Niratan.Services.Shortcuts;
using Niratan.ViewModels.Pages;

namespace Niratan.Tests.ViewModels.Pages;

public sealed class KeyboardShortcutsSettingsPageViewModelTests
{
    [Fact]
    public void RecordCommand_EntersRecordingUntilAKeyIsCaptured()
    {
        var shortcuts = new RecordingShortcutService();
        var viewModel = new KeyboardShortcutsSettingsPageViewModel(shortcuts);
        var row = viewModel.Rows[0];

        row.RecordCommand.Execute(null);

        viewModel.IsRecording.Should().BeTrue();
        viewModel.RecordingRow.Should().BeSameAs(row);
        row.IsRecording.Should().BeTrue();

        viewModel.CaptureShortcut(new KeyboardShortcutBinding("F4"));

        viewModel.IsRecording.Should().BeFalse();
        shortcuts.LastAction.Should().Be(row.Action);
        shortcuts.LastBinding.Should().Be(new KeyboardShortcutBinding("F4"));
    }

    [Fact]
    public void CancelRecordingCommand_LeavesTheBindingUnchanged()
    {
        var shortcuts = new RecordingShortcutService();
        var viewModel = new KeyboardShortcutsSettingsPageViewModel(shortcuts);
        var row = viewModel.Rows[0];

        row.RecordCommand.Execute(null);
        viewModel.CancelRecordingCommand.Execute(null);

        viewModel.IsRecording.Should().BeFalse();
        row.IsRecording.Should().BeFalse();
        shortcuts.LastAction.Should().BeNull();
    }

    private sealed class RecordingShortcutService : IShortcutService
    {
        public ShortcutRegistry Registry { get; } = ShortcutRegistry.Application;
        public int DictionaryEntryJumpCount => 1;
        public ShortcutAction? LastAction { get; private set; }
        public KeyboardShortcutBinding? LastBinding { get; private set; }

        public event EventHandler? ShortcutsChanged;

        public KeyboardShortcutBinding GetBinding(ShortcutAction action) => action.DefaultBinding;

        public bool TryResolve(
            ShortcutScope scope,
            KeyboardShortcutBinding binding,
            out ShortcutAction? action)
        {
            action = null;
            return false;
        }

        public IReadOnlyList<ShortcutConflict> GetConflicts(
            ShortcutAction action,
            KeyboardShortcutBinding? proposedBinding = null) => [];

        public void SetBinding(ShortcutAction action, KeyboardShortcutBinding binding)
        {
            LastAction = action;
            LastBinding = binding;
            ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        }

        public void ResetBinding(ShortcutAction action)
        {
        }

        public void SetDictionaryEntryJumpCount(int count)
        {
        }
    }
}
