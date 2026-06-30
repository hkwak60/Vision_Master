using System.ComponentModel;
using System.Runtime.CompilerServices;
using KickoutMonitor.Domain;
using KickoutMonitor.Infrastructure;

namespace KickoutMonitor.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly JsonSettingsStore _store;
    private string _settingsJson = string.Empty;
    private string _status = "Ready";
    private bool _isBusy;

    public SettingsViewModel(JsonSettingsStore store, VisionMasterSettings settings, string? startupWarning)
    {
        _store = store;
        SettingsJson = store.Serialize(settings);
        Status = string.IsNullOrWhiteSpace(startupWarning)
            ? $"Loaded settings: {store.SettingsPath}"
            : startupWarning;
        SaveCommand = new(SaveAsync, () => !IsBusy);
        ReloadCommand = new(ReloadAsync, () => !IsBusy);
        ResetCommand = new(ResetAsync, () => !IsBusy);
    }

    public string SettingsPath => _store.SettingsPath;
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand ReloadCommand { get; }
    public AsyncRelayCommand ResetCommand { get; }

    public string SettingsJson
    {
        get => _settingsJson;
        set => Set(ref _settingsJson, value);
    }

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value)) System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var settings = _store.DeserializeAndValidate(SettingsJson);
            await _store.SaveAsync(settings, CancellationToken.None);
            SettingsJson = _store.Serialize(settings);
            Status = $"Saved settings: {SettingsPath}";
        }
        catch (Exception exception)
        {
            Status = $"Save blocked: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var settings = await _store.LoadOrCreateAsync(CancellationToken.None);
            SettingsJson = _store.Serialize(settings);
            Status = string.IsNullOrWhiteSpace(_store.LastWarning)
                ? $"Reloaded settings: {SettingsPath}"
                : _store.LastWarning;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResetAsync()
    {
        IsBusy = true;
        try
        {
            var defaults = VisionMasterSettings.CreateDefault();
            await _store.SaveAsync(defaults, CancellationToken.None);
            SettingsJson = _store.Serialize(defaults);
            Status = "Reset settings to defaults.";
        }
        catch (Exception exception)
        {
            Status = $"Reset failed: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
