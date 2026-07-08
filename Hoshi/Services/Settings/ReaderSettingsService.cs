using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Hoshi.Helpers;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Settings;

internal class ReaderSettingsService : IReaderSettingsService
{
    private readonly ILogger<ReaderSettingsService> _logger;
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions;
    private ReaderSettings _current = new();

    public ReaderSettings Current => _current;
    public event EventHandler<SettingsChangedEventArgs>? SettingChanged;

    public ReaderSettingsService(ILogger<ReaderSettingsService> logger)
    {
        _logger = logger;
        _filePath = Path.Combine(AppDataHelper.GetAppDataPath(), "reader-settings.json");

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
            _current = new ReaderSettings();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_filePath);
            _current = JsonSerializer.Deserialize<ReaderSettings>(json, _jsonOptions)
                       ?? new ReaderSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load reader settings — using defaults");
            _current = new ReaderSettings();
        }
    }

    public async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_current, _jsonOptions);
        var tmpPath = _filePath + ".tmp";

        try
        {
            await File.WriteAllTextAsync(tmpPath, json);
            File.Move(tmpPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save reader settings");
        }
    }

    public void Set<T>(Expression<Func<ReaderSettings, T>> selector, T value)
    {
        Expression body = selector.Body;
        if (body is UnaryExpression unary)
            body = unary.Operand;

        if (body is not MemberExpression memberExpr)
            throw new ArgumentException(
                "Selector must be a property access expression.", nameof(selector));

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
            });
    }

    public void ReplaceCurrent(ReaderSettings settings)
    {
        var oldValue = _current;
        _current = settings ?? new ReaderSettings();

        SettingChanged?.Invoke(
            this,
            new SettingsChangedEventArgs
            {
                PropertyName = nameof(Current),
                OldValue = oldValue,
                NewValue = _current,
            });
    }

    public void Reset() => _current = new ReaderSettings();
}
