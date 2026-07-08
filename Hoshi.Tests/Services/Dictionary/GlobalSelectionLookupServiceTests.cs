using System.Linq.Expressions;
using FluentAssertions;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;
using Hoshi.Services.Dictionary;
using Hoshi.Services.Settings;

namespace Hoshi.Tests.Services.Dictionary;

public class GlobalSelectionLookupServiceTests
{
    [Fact]
    public async Task InitializeAsync_WhenDisabled_DoesNotRegisterHotKeyOrReadSelection()
    {
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                GlobalLookup = new GlobalLookupSettings { Enabled = false },
            },
        };
        var hotKeys = new RecordingGlobalLookupHotKeyRegistrar();
        var reader = new RecordingSelectedTextReader { Text = "星" };
        var popups = new RecordingGlobalLookupPopupService();
        var sut = new GlobalSelectionLookupService(settings, hotKeys, reader, popups);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        await hotKeys.TriggerAsync();

        hotKeys.RegisteredHotKeys.Should().BeEmpty();
        hotKeys.UnregisterCount.Should().Be(1);
        sut.StatusText.Should().Be("Global lookup disabled.");
        reader.ReadCount.Should().Be(0);
        popups.ShownQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task RegisteredTrigger_WhenEnabled_ReadsSelectedTextOnceAndShowsPopup()
    {
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                GlobalLookup = new GlobalLookupSettings { Enabled = true },
            },
        };
        var hotKeys = new RecordingGlobalLookupHotKeyRegistrar();
        var reader = new RecordingSelectedTextReader { Text = " 星 " };
        var popups = new RecordingGlobalLookupPopupService();
        var sut = new GlobalSelectionLookupService(settings, hotKeys, reader, popups);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        await hotKeys.TriggerAsync();

        hotKeys.RegisteredHotKeys.Should().Equal("Ctrl+Alt+D");
        hotKeys.UnregisterCount.Should().Be(1);
        sut.StatusText.Should().Be("Global lookup hotkey registered: Ctrl+Alt+D.");
        reader.ReadCount.Should().Be(1);
        popups.ShownQueries.Should().Equal("星");
    }

    [Fact]
    public async Task InitializeAsync_WhenRegistrarUnsupported_ReportsUnavailableStatus()
    {
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                GlobalLookup = new GlobalLookupSettings { Enabled = true },
            },
        };
        var hotKeys = new UnsupportedGlobalLookupHotKeyRegistrar();
        var reader = new RecordingSelectedTextReader { Text = "星" };
        var popups = new RecordingGlobalLookupPopupService();
        var sut = new GlobalSelectionLookupService(settings, hotKeys, reader, popups);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        sut.StatusText.Should().Be("Global lookup hotkey registration is not available in this build.");
        reader.ReadCount.Should().Be(0);
        popups.ShownQueries.Should().BeEmpty();
    }

    [Fact]
    public void Constructor_DoesNotDependOnManualLookupWindowService()
    {
        var parameters = typeof(GlobalSelectionLookupService)
            .GetConstructors()
            .Single()
            .GetParameters();

        parameters.Should().NotContain(parameter =>
            parameter.ParameterType == typeof(IGlobalLookupWindowService));
    }

    [Fact]
    public async Task InitializeAsync_WhenEnabledAgain_UnregistersBeforeRegistering()
    {
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                GlobalLookup = new GlobalLookupSettings { Enabled = true },
            },
        };
        var hotKeys = new RecordingGlobalLookupHotKeyRegistrar();
        var sut = new GlobalSelectionLookupService(
            settings,
            hotKeys,
            new RecordingSelectedTextReader(),
            new RecordingGlobalLookupPopupService());

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        await sut.InitializeAsync(TestContext.Current.CancellationToken);

        hotKeys.UnregisterCount.Should().Be(2);
        hotKeys.RegisteredHotKeys.Should().Equal("Ctrl+Alt+D", "Ctrl+Alt+D");
    }

    [Fact]
    public async Task RegisteredTrigger_WithEmptySelection_DoesNotOpenPopupOrWindow()
    {
        var settings = new RecordingSettingsService
        {
            Current = new AppSettings
            {
                GlobalLookup = new GlobalLookupSettings { Enabled = true },
            },
        };
        var hotKeys = new RecordingGlobalLookupHotKeyRegistrar();
        var reader = new RecordingSelectedTextReader { Text = "   " };
        var popups = new RecordingGlobalLookupPopupService();
        var sut = new GlobalSelectionLookupService(settings, hotKeys, reader, popups);

        await sut.InitializeAsync(TestContext.Current.CancellationToken);
        await hotKeys.TriggerAsync();

        reader.ReadCount.Should().Be(1);
        popups.ShownQueries.Should().BeEmpty();
    }

    [Fact]
    public async Task CascadingSelectedTextReader_WhenFirstReaderHasText_DoesNotReadFallback()
    {
        var first = new RecordingSelectedTextReader { Text = "original" };
        var second = new RecordingSelectedTextReader { Text = "fallback" };
        var sut = new CascadingSelectedTextReader(first, second);

        var text = await sut.TryReadSelectedTextAsync(TestContext.Current.CancellationToken);

        text.Should().Be("original");
        first.ReadCount.Should().Be(1);
        second.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task CascadingSelectedTextReader_WhenFirstReaderIsEmpty_UsesFallback()
    {
        var first = new RecordingSelectedTextReader { Text = " " };
        var second = new RecordingSelectedTextReader { Text = "fallback" };
        var sut = new CascadingSelectedTextReader(first, second);

        var text = await sut.TryReadSelectedTextAsync(TestContext.Current.CancellationToken);

        text.Should().Be("fallback");
        first.ReadCount.Should().Be(1);
        second.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task CascadingSelectedTextReader_WhenFirstReaderFails_UsesFallback()
    {
        var first = new ThrowingSelectedTextReader();
        var second = new RecordingSelectedTextReader { Text = "fallback" };
        var sut = new CascadingSelectedTextReader(first, second);

        var text = await sut.TryReadSelectedTextAsync(TestContext.Current.CancellationToken);

        text.Should().Be("fallback");
        first.ReadCount.Should().Be(1);
        second.ReadCount.Should().Be(1);
    }

    private sealed class RecordingSettingsService : ISettingsService
    {
        public AppSettings Current { get; init; } = new();

        public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

        public void Set<T>(Expression<Func<AppSettings, T>> selector, T value) =>
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs
            {
                PropertyName = selector.ToString() ?? "",
                NewValue = value,
            });

        public void ReplaceCurrent(AppSettings settings) =>
            SettingChanged?.Invoke(this, new SettingsChangedEventArgs
            {
                PropertyName = nameof(Current),
                NewValue = settings,
            });

        public Task SaveAsync() => Task.CompletedTask;

        public Task LoadAsync() => Task.CompletedTask;

        public void Reset()
        {
        }
    }

    private sealed class RecordingGlobalLookupHotKeyRegistrar : IGlobalLookupHotKeyRegistrar
    {
        private Func<CancellationToken, Task>? _handler;

        public List<string> RegisteredHotKeys { get; } = [];
        public int UnregisterCount { get; private set; }

        public Task RegisterAsync(
            string hotKey,
            Func<CancellationToken, Task> handler,
            CancellationToken ct = default)
        {
            RegisteredHotKeys.Add(hotKey);
            _handler = handler;
            return Task.CompletedTask;
        }

        public Task TriggerAsync() =>
            _handler?.Invoke(TestContext.Current.CancellationToken) ?? Task.CompletedTask;

        public void Unregister()
        {
            UnregisterCount++;
        }
    }

    private sealed class UnsupportedGlobalLookupHotKeyRegistrar : IGlobalLookupHotKeyRegistrar
    {
        public Task RegisterAsync(
            string hotKey,
            Func<CancellationToken, Task> handler,
            CancellationToken ct = default) =>
            throw new NotSupportedException("not available");

        public void Unregister()
        {
        }
    }

    private sealed class RecordingSelectedTextReader : ISelectedTextReader
    {
        public string? Text { get; init; }
        public int ReadCount { get; private set; }

        public Task<string?> TryReadSelectedTextAsync(CancellationToken ct = default)
        {
            ReadCount++;
            return Task.FromResult(Text);
        }
    }

    private sealed class ThrowingSelectedTextReader : ISelectedTextReader
    {
        public int ReadCount { get; private set; }

        public Task<string?> TryReadSelectedTextAsync(CancellationToken ct = default)
        {
            ReadCount++;
            throw new InvalidOperationException("reader failed");
        }
    }

    private sealed class RecordingGlobalLookupPopupService : IGlobalLookupPopupService
    {
        public List<string> ShownQueries { get; } = [];

        public Task ShowAsync(string query, CancellationToken ct = default)
        {
            ShownQueries.Add(query);
            return Task.CompletedTask;
        }
    }

}
