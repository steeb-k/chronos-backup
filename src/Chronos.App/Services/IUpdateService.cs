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
    /// Gets or sets the download URL for the latest release.
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
    /// Opens the download page for the latest release in the default browser.
    /// </summary>
    void OpenDownloadPage();

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
