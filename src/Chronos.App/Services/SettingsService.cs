using System.Text.Json;
using Chronos.Common.Helpers;
using Serilog;

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
    /// Gets or sets the theme mode: 0 = System, 1 = Light, 2 = Dark.
    /// </summary>
    int ThemeMode { get; set; }
}

/// <summary>
/// Implementation of settings service using a JSON file in the local app data folder.
/// Works for both packaged and unpackaged WinUI 3 apps.
/// </summary>
public class SettingsService : ISettingsService
{
    private static readonly string SettingsDir = PeEnvironment.GetAppDataDirectory();
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

    public int DefaultCompressionLevel { get; set; } = 3;
    public string DefaultBackupPath { get; set; } = string.Empty;
    public bool UseVssByDefault { get; set; } = true;
    public bool VerifyByDefault { get; set; } = true;
    public int ThemeMode { get; set; } = 0; // 0=System

    public SettingsService()
    {
        LoadSettings();
    }

    public void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return;

            var json = File.ReadAllText(SettingsPath);
            var data = JsonSerializer.Deserialize<SettingsData>(json);
            if (data is null) return;

            DefaultCompressionLevel = data.DefaultCompressionLevel;
            DefaultBackupPath = data.DefaultBackupPath ?? string.Empty;
            UseVssByDefault = data.UseVssByDefault;
            VerifyByDefault = data.VerifyByDefault;
            ThemeMode = data.ThemeMode;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to load settings from {Path}", SettingsPath);
        }
    }

    public void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);

            var data = new SettingsData
            {
                DefaultCompressionLevel = DefaultCompressionLevel,
                DefaultBackupPath = DefaultBackupPath,
                UseVssByDefault = UseVssByDefault,
                VerifyByDefault = VerifyByDefault,
                ThemeMode = ThemeMode
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save settings to {Path}", SettingsPath);
        }
    }

    private sealed class SettingsData
    {
        public int DefaultCompressionLevel { get; set; } = 3;
        public string? DefaultBackupPath { get; set; }
        public bool UseVssByDefault { get; set; } = true;
        public bool VerifyByDefault { get; set; } = true;
        public int ThemeMode { get; set; } = 0;
    }
}
