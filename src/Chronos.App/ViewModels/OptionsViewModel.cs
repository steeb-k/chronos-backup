using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Chronos.App.Services;
using Chronos.Common.Extensions;
using Microsoft.UI.Xaml;
using Serilog;
using System.IO;

namespace Chronos.App.ViewModels;

public partial class OptionsViewModel : ObservableObject
{
    private readonly ISettingsService? _settingsService;
    private readonly IUpdateService? _updateService;
    private bool _isLoading;

    [ObservableProperty] public partial int DefaultCompressionLevel { get; set; } = 3;
    [ObservableProperty] public partial string DefaultBackupPath { get; set; } = string.Empty;
    [ObservableProperty] public partial List<TargetDriveInfo> AvailableTargetDrives { get; set; } = new();
    [ObservableProperty] public partial TargetDriveInfo? SelectedDefaultBackupDrive { get; set; }
    [ObservableProperty] public partial bool UseVssByDefault { get; set; } = true;
    [ObservableProperty] public partial bool VerifyByDefault { get; set; } = true;

    /// <summary>0 = System, 1 = Light, 2 = Dark</summary>
    [ObservableProperty] public partial int ThemeMode { get; set; } = 0;

    // Update-related properties
    [ObservableProperty] public partial string CurrentVersion { get; set; } = "0.0.0";
    [ObservableProperty] public partial string? LatestVersion { get; set; }
    [ObservableProperty] public partial bool IsUpdateAvailable { get; set; }
    [ObservableProperty] public partial bool IsCheckingForUpdates { get; set; }
    [ObservableProperty] public partial bool IsDownloadingUpdate { get; set; }
    [ObservableProperty] public partial int DownloadProgress { get; set; }
    [ObservableProperty] public partial string UpdateStatusMessage { get; set; } = string.Empty;

    public OptionsViewModel(ISettingsService? settingsService = null, IUpdateService? updateService = null)
    {
        _settingsService = settingsService;
        _updateService = updateService;
        
        // Initialize version info
        if (_updateService != null)
        {
            CurrentVersion = _updateService.CurrentVersion;
            _updateService.UpdateCheckCompleted += OnUpdateCheckCompleted;
        }
        
        LoadTargetDrives();
        LoadSettings();
    }

    [RelayCommand]
    private void LoadTargetDrives()
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
            .Select(d => new TargetDriveInfo
            {
                DriveLetter = d.Name.TrimEnd('\\'),
                VolumeLabel = d.VolumeLabel,
                FreeSpaceBytes = d.AvailableFreeSpace,
                TotalSizeBytes = d.TotalSize
            })
            .OrderBy(d => d.DriveLetter)
            .ToList();
        
        AvailableTargetDrives = drives;
    }

    private void LoadSettings()
    {
        if (_settingsService is null)
            return;

        _isLoading = true;
        try
        {
            DefaultCompressionLevel = _settingsService.DefaultCompressionLevel;
            DefaultBackupPath = _settingsService.DefaultBackupPath;
            UseVssByDefault = _settingsService.UseVssByDefault;
            VerifyByDefault = _settingsService.VerifyByDefault;
            ThemeMode = _settingsService.ThemeMode;

            // Select the drive that matches the stored DefaultBackupPath
            if (!string.IsNullOrEmpty(DefaultBackupPath) && DefaultBackupPath.Length >= 2)
            {
                var driveLetter = DefaultBackupPath.Substring(0, 2); // e.g., "D:"
                SelectedDefaultBackupDrive = AvailableTargetDrives.FirstOrDefault(d => 
                    d.DriveLetter.StartsWith(driveLetter, StringComparison.OrdinalIgnoreCase));
            }
            
            // If no match or no saved path, select first non-C drive
            SelectedDefaultBackupDrive ??= AvailableTargetDrives.FirstOrDefault(d => 
                !d.DriveLetter.StartsWith("C", StringComparison.OrdinalIgnoreCase))
                ?? AvailableTargetDrives.FirstOrDefault();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void SaveSettings()
    {
        if (_settingsService is null || _isLoading)
            return;

        _settingsService.DefaultCompressionLevel = DefaultCompressionLevel;
        _settingsService.DefaultBackupPath = DefaultBackupPath;
        _settingsService.UseVssByDefault = UseVssByDefault;
        _settingsService.VerifyByDefault = VerifyByDefault;
        _settingsService.ThemeMode = ThemeMode;

        _settingsService.SaveSettings();
    }

    partial void OnDefaultCompressionLevelChanged(int value) => SaveSettings();
    partial void OnDefaultBackupPathChanged(string value) => SaveSettings();
    partial void OnUseVssByDefaultChanged(bool value) => SaveSettings();
    partial void OnVerifyByDefaultChanged(bool value) => SaveSettings();

    partial void OnSelectedDefaultBackupDriveChanged(TargetDriveInfo? value)
    {
        if (value is not null)
        {
            DefaultBackupPath = value.DriveLetter + "\\";
        }
    }

    partial void OnThemeModeChanged(int value)
    {
        SaveSettings();
        ApplyTheme();
    }

    /// <summary>
    /// Applies the current theme to the app's root FrameworkElement.
    /// </summary>
    public void ApplyTheme()
    {
        var theme = ThemeMode switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };

        try
        {
            if (App.MainWindow?.Content is FrameworkElement root)
            {
                root.RequestedTheme = theme;
                Log.Debug("Applied theme: {Theme}", theme);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to apply theme");
        }
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (_updateService is null || IsCheckingForUpdates)
            return;

        IsCheckingForUpdates = true;
        UpdateStatusMessage = "Checking for updates...";

        try
        {
            await _updateService.CheckForUpdatesAsync();
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    [RelayCommand]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_updateService is null || IsDownloadingUpdate)
            return;

        IsDownloadingUpdate = true;
        DownloadProgress = 0;
        UpdateStatusMessage = "Downloading installer...";

        try
        {
            var progress = new Progress<int>(percent =>
            {
                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    DownloadProgress = percent;
                    UpdateStatusMessage = $"Downloading installer... {percent}%";
                });
            });

            var installerPath = await _updateService.DownloadInstallerAsync(progress);

            if (string.IsNullOrEmpty(installerPath))
            {
                UpdateStatusMessage = "Download failed. Please try again.";
                return;
            }

            UpdateStatusMessage = "Download complete. Launching installer...";
            _updateService.LaunchInstallerAndExit(installerPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download and install update");
            UpdateStatusMessage = $"Update failed: {ex.Message}";
        }
        finally
        {
            IsDownloadingUpdate = false;
        }
    }

    private void OnUpdateCheckCompleted(object? sender, UpdateCheckEventArgs e)
    {
        // Update on UI thread
        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            if (e.IsError)
            {
                UpdateStatusMessage = e.ErrorMessage ?? "Error checking for updates";
                IsUpdateAvailable = false;
            }
            else if (e.UpdateAvailable)
            {
                LatestVersion = e.LatestVersion;
                IsUpdateAvailable = true;
                UpdateStatusMessage = $"Version {e.LatestVersion} is available!";
            }
            else
            {
                LatestVersion = e.LatestVersion;
                IsUpdateAvailable = false;
                UpdateStatusMessage = "You're running the latest version.";
            }
        });
    }
}

