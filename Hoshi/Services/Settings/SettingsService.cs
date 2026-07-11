using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Helpers;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Settings;

internal class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;

    private readonly string _filePath;
    private readonly Func<string, string, Task> _writeAllTextAsync;
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;
    private AppSettings _current = new();

    public AppSettings Current => _current;
    public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

    public SettingsService(ILogger<SettingsService> logger)
        : this(
            logger,
            Path.Combine(AppDataHelper.GetAppDataPath(), "settings.json"),
            static (path, contents) => File.WriteAllTextAsync(path, contents))
    {
    }

    internal SettingsService(
        ILogger<SettingsService> logger,
        string filePath,
        Func<string, string, Task> writeAllTextAsync)
    {
        _logger = logger;
        _filePath = filePath;
        _writeAllTextAsync = writeAllTextAsync;

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };
    }

    public async Task LoadAsync()
    {
        if (!File.Exists(_filePath))
        {
            _current = new AppSettings();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            _current =
                JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load settings — using defaults");
            _current = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_current, _jsonOptions);
        var tmpPath = _filePath + ".tmp";

        await _saveSemaphore.WaitAsync();
        try
        {
            await _writeAllTextAsync(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    // Always use Set() to mutate settings instead of accessing Current properties directly.
    // Set() fires SettingChanged, which MainWindow listens to for applying theme changes at runtime.
    // Direct mutation via Current bypasses the event and changes won't be reflected until restart.
    public void Set<T>(Expression<Func<AppSettings, T>> selector, T value)
    {
        Expression body = selector.Body;
        if (body is UnaryExpression unary)
            body = unary.Operand;

        if (body is not MemberExpression memberExpr)
            throw new ArgumentException(
                "Selector must be a property access expression.",
                nameof(selector)
            );

        if (memberExpr.Member is not PropertyInfo propInfo)
            throw new ArgumentException("Selector must target a property.", nameof(selector));

        var oldValue = propInfo.GetValue(_current);
        propInfo.SetValue(_current, value);

        SettingChanged?.Invoke(
            this,
            new SettingsChangedEventArgs
            {
                PropertyName = propInfo.Name,
                OldValue = oldValue,
                NewValue = value,
            }
        );
    }

    public void ReplaceCurrent(AppSettings settings)
    {
        var oldValue = _current;
        _current = settings ?? new AppSettings();

        SettingChanged?.Invoke(
            this,
            new SettingsChangedEventArgs
            {
                PropertyName = nameof(Current),
                OldValue = oldValue,
                NewValue = _current,
            }
        );
    }

    public void Reset() => _current = new AppSettings();
}
