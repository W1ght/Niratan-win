using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Settings;

public interface ISettingsService
{
    AppSettings Current { get; }

    void Set<T>(Expression<Func<AppSettings, T>> selector, T value);
    void ReplaceCurrent(AppSettings settings);

    Task SaveAsync();
    Task LoadAsync();
    void Reset();

    event EventHandler<SettingsChangedEventArgs> SettingChanged;
}
