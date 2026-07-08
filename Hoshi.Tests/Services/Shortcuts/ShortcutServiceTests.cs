using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;
using Hoshi.Models.Shortcuts;
using Hoshi.Services.Settings;
using Hoshi.Services.Shortcuts;

namespace Hoshi.Tests.Services.Shortcuts;

public sealed class ShortcutServiceTests
{
    [Fact]
    public void TryResolve_UsesUserConfiguredVideoBinding()
    {
        var settings = new RecordingSettingsService();
        var service = new ShortcutService(settings);
        var action = service.Registry.Action(VideoShortcutActions.LookupSubtitleId)!;

        service.SetBinding(action, new KeyboardShortcutBinding("l"));

        service.TryResolve(
                ShortcutScope.Video,
                new KeyboardShortcutBinding("l"),
                out var resolved)
            .Should()
            .BeTrue();
        resolved.Should().Be(action);
        settings.SaveCount.Should().Be(1);
    }

    [Fact]
    public void ResetBinding_RestoresDefaultBinding()
    {
        var settings = new RecordingSettingsService();
        var service = new ShortcutService(settings);
        var action = service.Registry.Action(VideoShortcutActions.SeekForwardId)!;

        service.SetBinding(action, new KeyboardShortcutBinding("j"));
        service.ResetBinding(action);

        service.GetBinding(action).Label.Should().Be("\u2192");
        settings.Current.ShortcutConfiguration.Bindings.Should().NotContainKey(action.Id);
    }

    [Fact]
    public void GetConflicts_ReportsSameVideoScopeBinding()
    {
        var settings = new RecordingSettingsService();
        var service = new ShortcutService(settings);
        var lookup = service.Registry.Action(VideoShortcutActions.LookupSubtitleId)!;

        var conflicts = service.GetConflicts(
            lookup,
            VideoShortcutActions.ToggleFullscreen.DefaultBinding);

        conflicts.Should().ContainSingle(conflict =>
            conflict.Kind == ShortcutConflictKind.Conflict
            && conflict.Action.Id == VideoShortcutActions.ToggleFullscreenId);
    }

    private sealed class RecordingSettingsService : ISettingsService
    {
        public AppSettings Current { get; private set; } = new();
        public int SaveCount { get; private set; }

        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value)
        {
            if (selector.Body is not MemberExpression member)
                throw new InvalidOperationException("Only direct settings properties are supported in tests.");

            var property = typeof(AppSettings).GetProperty(member.Member.Name)
                ?? throw new InvalidOperationException("Unknown settings property.");
            property.SetValue(Current, value);
            SettingChanged?.Invoke(
                this,
                new SettingsChangedEventArgs
                {
                    PropertyName = property.Name,
                    NewValue = value,
                });
        }

        public Task SaveAsync()
        {
            SaveCount++;
            return Task.CompletedTask;
        }

        public Task LoadAsync() => Task.CompletedTask;
        public void Reset() => Current = new AppSettings();
    }
}
