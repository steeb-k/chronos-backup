namespace Chronos.App.Services;

/// <summary>
/// Service for checking and managing application updates from GitHub releases.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Gets the current version of the application.
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>
    /// Gets or sets the latest available version (null if not checked yet).
    /// </summary>
    string? LatestVersion { get; }

    /// <summary>
    /// Gets or sets the download URL for the latest release installer.
    /// </summary>
    string? DownloadUrl { get; }

    /// <summary>
    /// Gets or sets the release notes for the latest version.
    /// </summary>
    string? ReleaseNotes { get; }

    /// <summary>
    /// Gets whether an update is available.
    /// </summary>
    bool IsUpdateAvailable { get; }

    /// <summary>
    /// Checks for updates from GitHub releases.
    /// </summary>
    /// <returns>True if a newer version is available, false otherwise.</returns>
    Task<bool> CheckForUpdatesAsync();

    /// <summary>
    /// Downloads the installer to a temp path and returns the file path.
    /// </summary>
    /// <param name="progress">Progress callback (0-100).</param>
    /// <returns>The path to the downloaded installer, or null on failure.</returns>
    Task<string?> DownloadInstallerAsync(IProgress<int>? progress = null);

    /// <summary>
    /// Launches the downloaded installer and exits the application.
    /// </summary>
    /// <param name="installerPath">Path to the downloaded installer exe.</param>
    void LaunchInstallerAndExit(string installerPath);

    /// <summary>
    /// Event raised when update check status changes.
    /// </summary>
    event EventHandler<UpdateCheckEventArgs>? UpdateCheckCompleted;
}

/// <summary>
/// Event arguments for update check completion.
/// </summary>
public class UpdateCheckEventArgs : EventArgs
{
    public bool UpdateAvailable { get; init; }
    public string? LatestVersion { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsError => !string.IsNullOrEmpty(ErrorMessage);
}
