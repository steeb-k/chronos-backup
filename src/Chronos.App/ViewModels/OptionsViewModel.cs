using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.App.Services;

namespace Chronos.App.ViewModels;

public partial class OptionsViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;

    [ObservableProperty] public partial int DefaultCompressionLevel { get; set; } = 3;
    [ObservableProperty] public partial string DefaultBackupPath { get; set; } = string.Empty;
    [ObservableProperty] public partial bool UseVssByDefault { get; set; } = true;
    [ObservableProperty] public partial bool VerifyByDefault { get; set; } = true;
    [ObservableProperty] public partial bool UseDarkTheme { get; set; } = true;

    public OptionsViewModel(ISettingsService? settingsService = null)
    {
        _settingsService = settingsService;
        LoadSettings();
    }

    private void LoadSettings()
    {
        if (_settingsService is null)
            return;

        DefaultCompressionLevel = _settingsService.DefaultCompressionLevel;
        DefaultBackupPath = _settingsService.DefaultBackupPath;
        UseVssByDefault = _settingsService.UseVssByDefault;
        VerifyByDefault = _settingsService.VerifyByDefault;
        UseDarkTheme = _settingsService.UseDarkTheme;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        if (_settingsService is null)
            return;

        _settingsService.DefaultCompressionLevel = DefaultCompressionLevel;
        _settingsService.DefaultBackupPath = DefaultBackupPath;
        _settingsService.UseVssByDefault = UseVssByDefault;
        _settingsService.VerifyByDefault = VerifyByDefault;
        _settingsService.UseDarkTheme = UseDarkTheme;

        _settingsService.SaveSettings();
    }

    partial void OnDefaultCompressionLevelChanged(int value)
    {
        SaveSettings();
    }

    partial void OnDefaultBackupPathChanged(string value)
    {
        SaveSettings();
    }

    partial void OnUseVssByDefaultChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnVerifyByDefaultChanged(bool value)
    {
        SaveSettings();
    }

    partial void OnUseDarkThemeChanged(bool value)
    {
        SaveSettings();
    }
}

