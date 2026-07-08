using System;
using System.Collections.Generic;
using Hoshi.Models.Shortcuts;

namespace Hoshi.Services.Shortcuts;

public interface IShortcutService
{
    ShortcutRegistry Registry { get; }

    event EventHandler? ShortcutsChanged;

    KeyboardShortcutBinding GetBinding(ShortcutAction action);

    bool TryResolve(
        ShortcutScope scope,
        KeyboardShortcutBinding binding,
        out ShortcutAction? action);

    IReadOnlyList<ShortcutConflict> GetConflicts(
        ShortcutAction action,
        KeyboardShortcutBinding? proposedBinding = null);

    void SetBinding(ShortcutAction action, KeyboardShortcutBinding binding);
    void ResetBinding(ShortcutAction action);
}
