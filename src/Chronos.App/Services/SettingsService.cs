using Windows.Storage;

namespace Chronos.App.Services;

/// <summary>
/// Service for persisting and loading application settings.
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// Loads settings from storage.
    /// </summary>
    void LoadSettings();

    /// <summary>
    /// Saves settings to storage.
    /// </summary>
    void SaveSettings();

    /// <summary>
    /// Gets or sets the default compression level (0-22).
    /// </summary>
    int DefaultCompressionLevel { get; set; }

    /// <summary>
    /// Gets or sets the default backup path.
    /// </summary>
    string DefaultBackupPath { get; set; }

    /// <summary>
    /// Gets or sets whether to use VSS by default.
    /// </summary>
    bool UseVssByDefault { get; set; }

    /// <summary>
    /// Gets or sets whether to verify backups by default.
    /// </summary>
    bool VerifyByDefault { get; set; }

    /// <summary>
    /// Gets or sets whether to use dark theme.
    /// </summary>
    bool UseDarkTheme { get; set; }
}

/// <summary>
/// Implementation of settings service using ApplicationData.LocalSettings.
/// </summary>
public class SettingsService : ISettingsService
{
    private readonly ApplicationDataContainer _localSettings;

    private const string KeyDefaultCompressionLevel = "DefaultCompressionLevel";
    private const string KeyDefaultBackupPath = "DefaultBackupPath";
    private const string KeyUseVssByDefault = "UseVssByDefault";
    private const string KeyVerifyByDefault = "VerifyByDefault";
    private const string KeyUseDarkTheme = "UseDarkTheme";

    public int DefaultCompressionLevel { get; set; } = 3;
    public string DefaultBackupPath { get; set; } = string.Empty;
    public bool UseVssByDefault { get; set; } = true;
    public bool VerifyByDefault { get; set; } = true;
    public bool UseDarkTheme { get; set; } = true;

    public SettingsService()
    {
        _localSettings = ApplicationData.Current.LocalSettings;
        LoadSettings();
    }

    public void LoadSettings()
    {
        DefaultCompressionLevel = GetValue(KeyDefaultCompressionLevel, 3);
        DefaultBackupPath = GetValue(KeyDefaultBackupPath, string.Empty);
        UseVssByDefault = GetValue(KeyUseVssByDefault, true);
        VerifyByDefault = GetValue(KeyVerifyByDefault, true);
        UseDarkTheme = GetValue(KeyUseDarkTheme, true);
    }

    public void SaveSettings()
    {
        SetValue(KeyDefaultCompressionLevel, DefaultCompressionLevel);
        SetValue(KeyDefaultBackupPath, DefaultBackupPath);
        SetValue(KeyUseVssByDefault, UseVssByDefault);
        SetValue(KeyVerifyByDefault, VerifyByDefault);
        SetValue(KeyUseDarkTheme, UseDarkTheme);
    }

    private T GetValue<T>(string key, T defaultValue)
    {
        if (_localSettings.Values.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    private void SetValue<T>(string key, T value)
    {
        _localSettings.Values[key] = value;
    }
}
