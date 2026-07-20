using System;
using System.Collections.Generic;
using System.Linq;
using Niratan.Models.Settings;
using Niratan.Models.Shortcuts;
using Niratan.Services.Settings;

namespace Niratan.Services.Shortcuts;

internal sealed class ShortcutService : IShortcutService
{
    private readonly ISettingsService _settingsService;

    public ShortcutService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public ShortcutRegistry Registry { get; } = ShortcutRegistry.Application;

    public event EventHandler? ShortcutsChanged;

    public KeyboardShortcutBinding GetBinding(ShortcutAction action) =>
        CurrentConfiguration.GetBinding(action);

    public int DictionaryEntryJumpCount =>
        Math.Clamp(CurrentConfiguration.DictionaryEntryJumpCount, 1, 10);

    public bool TryResolve(
        ShortcutScope scope,
        KeyboardShortcutBinding binding,
        out ShortcutAction? action)
    {
        action = null;
        if (binding.IsEmpty)
            return false;

        var configuration = CurrentConfiguration;
        action = Registry.Actions
            .Where(candidate => AppliesToScope(candidate, scope))
            .Where(candidate => configuration.GetBinding(candidate).Matches(binding))
            .OrderByDescending(candidate => configuration.HasCustomBinding(candidate.Id))
            .FirstOrDefault();

        return action != null;
    }

    public IReadOnlyList<ShortcutConflict> GetConflicts(
        ShortcutAction action,
        KeyboardShortcutBinding? proposedBinding = null)
    {
        var binding = proposedBinding ?? GetBinding(action);
        if (binding.IsEmpty)
            return [];

        return Registry.Actions
            .Where(candidate => candidate.Id != action.Id)
            .Select(candidate => new
            {
                Candidate = candidate,
                Binding = GetBinding(candidate),
                Kind = ShortcutConflictChecker.Relationship(
                    action,
                    binding,
                    candidate,
                    GetBinding(candidate)),
            })
            .Where(candidate => candidate.Kind != ShortcutConflictKind.None)
            .Select(candidate => new ShortcutConflict(
                candidate.Kind,
                candidate.Candidate,
                candidate.Binding))
            .ToList();
    }

    public void SetBinding(ShortcutAction action, KeyboardShortcutBinding binding)
    {
        var next = CurrentConfiguration.Clone();
        next.SetBinding(action.Id, binding);
        SaveConfiguration(next);
    }

    public void ResetBinding(ShortcutAction action)
    {
        var next = CurrentConfiguration.Clone();
        next.ResetBinding(action.Id);
        SaveConfiguration(next);
    }

    public void SetDictionaryEntryJumpCount(int count)
    {
        var next = CurrentConfiguration.Clone();
        next.DictionaryEntryJumpCount = Math.Clamp(count, 1, 10);
        SaveConfiguration(next);
    }

    private ShortcutConfiguration CurrentConfiguration =>
        _settingsService.Current.ShortcutConfiguration ?? new ShortcutConfiguration();

    private void SaveConfiguration(ShortcutConfiguration configuration)
    {
        _settingsService.Set(settings => settings.ShortcutConfiguration, configuration);
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);
        _ = _settingsService.SaveAsync();
    }

    private static bool AppliesToScope(ShortcutAction action, ShortcutScope scope) =>
        action.Scopes.Contains(scope) || action.Scopes.Contains(ShortcutScope.Global);
}
