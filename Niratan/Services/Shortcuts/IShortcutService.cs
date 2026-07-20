using System;
using System.Collections.Generic;
using Niratan.Models.Shortcuts;

namespace Niratan.Services.Shortcuts;

public interface IShortcutService
{
    ShortcutRegistry Registry { get; }

    event EventHandler? ShortcutsChanged;

    KeyboardShortcutBinding GetBinding(ShortcutAction action);

    int DictionaryEntryJumpCount => 1;

    bool TryResolve(
        ShortcutScope scope,
        KeyboardShortcutBinding binding,
        out ShortcutAction? action);

    IReadOnlyList<ShortcutConflict> GetConflicts(
        ShortcutAction action,
        KeyboardShortcutBinding? proposedBinding = null);

    void SetBinding(ShortcutAction action, KeyboardShortcutBinding binding);
    void ResetBinding(ShortcutAction action);

    void SetDictionaryEntryJumpCount(int count)
    {
    }
}
