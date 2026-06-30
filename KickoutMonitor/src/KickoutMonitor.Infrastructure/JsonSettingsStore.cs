using System.Text.Json;
using KickoutMonitor.Domain;

namespace KickoutMonitor.Infrastructure;

public sealed class JsonSettingsStore
{
    public JsonSettingsStore(string? settingsPath = null)
    {
        SettingsPath = settingsPath ?? Path.Combine(DefaultRoot(), "settings.json");
    }

    public string SettingsPath { get; }
    public string? LastWarning { get; private set; }

    public async Task<VisionMasterSettings> LoadOrCreateAsync(CancellationToken cancellationToken)
    {
        LastWarning = null;
        if (!File.Exists(SettingsPath))
        {
            var defaults = VisionMasterSettings.CreateDefault();
            await SaveAsync(defaults, cancellationToken);
            return defaults;
        }

        try
        {
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
            var settings = await JsonSerializer.DeserializeAsync<VisionMasterSettings>(stream, VisionMasterSettings.JsonOptions, cancellationToken)
                ?? VisionMasterSettings.CreateDefault();
            var errors = settings.Validate();
            if (errors.Count == 0) return settings;
            LastWarning = "Settings validation failed; using defaults: " + string.Join(" ", errors);
            return VisionMasterSettings.CreateDefault();
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            LastWarning = $"Settings load failed; using defaults: {exception.Message}";
            return VisionMasterSettings.CreateDefault();
        }
    }

    public async Task SaveAsync(VisionMasterSettings settings, CancellationToken cancellationToken)
    {
        var errors = settings.Validate();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = new FileStream(SettingsPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(stream, settings, VisionMasterSettings.JsonOptions, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public Task ResetToDefaultsAsync(CancellationToken cancellationToken) =>
        SaveAsync(VisionMasterSettings.CreateDefault(), cancellationToken);

    public string Serialize(VisionMasterSettings settings) =>
        JsonSerializer.Serialize(settings, VisionMasterSettings.JsonOptions);

    public VisionMasterSettings DeserializeAndValidate(string json)
    {
        var settings = JsonSerializer.Deserialize<VisionMasterSettings>(json, VisionMasterSettings.JsonOptions)
            ?? throw new InvalidOperationException("Settings JSON is empty.");
        var errors = settings.Validate();
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        return settings;
    }

    private static string DefaultRoot()
    {
        const string requested = @"E:\KWAK\VisionMaster";
        var driveRoot = Path.GetPathRoot(requested);
        if (!string.IsNullOrWhiteSpace(driveRoot) && Directory.Exists(driveRoot)) return requested;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KickoutMonitor");
    }
}
