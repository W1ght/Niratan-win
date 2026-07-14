using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Niratan.Models.DTO;
using Niratan.Models.Settings;

namespace Niratan.Services.Settings;

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
