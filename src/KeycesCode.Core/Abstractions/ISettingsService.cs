using KeycesCode.Core.Models;

namespace KeycesCode.Core.Abstractions;

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync();
    Task SaveAsync(AppSettings settings);
    event EventHandler<AppSettings>? SettingsChanged;
}