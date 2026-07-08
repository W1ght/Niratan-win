using System;
using System.Linq.Expressions;
using System.Threading.Tasks;
using Hoshi.Models.DTO;
using Hoshi.Models.Settings;

namespace Hoshi.Services.Settings;

public interface IReaderSettingsService
{
    ReaderSettings Current { get; }

    void Set<T>(Expression<Func<ReaderSettings, T>> selector, T value);
    void ReplaceCurrent(ReaderSettings settings);

    Task SaveAsync();
    Task LoadAsync();
    void Reset();

    event EventHandler<SettingsChangedEventArgs> SettingChanged;
}
