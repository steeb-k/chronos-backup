using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Serilog;

namespace Chronos.App.Services;

/// <summary>
/// Service for checking application updates from GitHub releases.
/// </summary>
public class UpdateService : IUpdateService
{
    private const string GitHubApiUrl = "https://api.github.com/repos/steeb-k/chronos-backup/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/steeb-k/chronos-backup/releases";

    private static readonly HttpClient _httpClient;

    static UpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Chronos-UpdateChecker");
        _httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _httpClient.Timeout = TimeSpan.FromSeconds(15);
    }

    public string CurrentVersion { get; }
    public string? LatestVersion { get; private set; }
    public string? DownloadUrl { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public bool IsUpdateAvailable { get; private set; }

    public event EventHandler<UpdateCheckEventArgs>? UpdateCheckCompleted;

    public UpdateService()
    {
        CurrentVersion = GetCurrentVersion();
        Log.Debug("UpdateService initialized. Current version: {Version}", CurrentVersion);
    }

    private static string GetCurrentVersion()
    {
        try
        {
            // Try to get version from assembly
            var assembly = Assembly.GetEntryAssembly();
            if (assembly != null)
            {
                var version = assembly.GetName().Version;
                if (version != null)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }

                // Fallback to informational version
                var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(infoVersion))
                {
                    // Remove any +buildinfo suffix
                    var plusIndex = infoVersion.IndexOf('+');
                    return plusIndex > 0 ? infoVersion[..plusIndex] : infoVersion;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to get version from assembly");
        }

        return "0.0.0";
    }

    public async Task<bool> CheckForUpdatesAsync()
    {
        Log.Information("Checking for updates...");

        try
        {
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(GitHubApiUrl);

            if (release is null)
            {
                Log.Warning("No release information received from GitHub");
                RaiseUpdateCheckCompleted(false, null, "No release information available");
                return false;
            }

            // Parse version from tag (remove 'v' prefix if present)
            var tagVersion = release.TagName?.TrimStart('v', 'V') ?? "0.0.0";
            LatestVersion = tagVersion;
            ReleaseNotes = release.Body;

            // Find the appropriate download URL (prefer x64 setup, then portable)
            DownloadUrl = release.Assets?
                .FirstOrDefault(a => a.Name?.Contains("x64-Setup", StringComparison.OrdinalIgnoreCase) == true)?.BrowserDownloadUrl
                ?? release.Assets?
                    .FirstOrDefault(a => a.Name?.Contains("x64-Portable", StringComparison.OrdinalIgnoreCase) == true)?.BrowserDownloadUrl
                ?? release.HtmlUrl;

            // Compare versions
            IsUpdateAvailable = CompareVersions(CurrentVersion, LatestVersion) < 0;

            Log.Information("Update check complete. Current: {Current}, Latest: {Latest}, UpdateAvailable: {Available}",
                CurrentVersion, LatestVersion, IsUpdateAvailable);

            RaiseUpdateCheckCompleted(IsUpdateAvailable, LatestVersion, null);
            return IsUpdateAvailable;
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Network error checking for updates");
            RaiseUpdateCheckCompleted(false, null, "Unable to connect to GitHub. Please check your internet connection.");
            return false;
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            Log.Warning("Update check timed out");
            RaiseUpdateCheckCompleted(false, null, "Update check timed out. Please try again later.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error checking for updates");
            RaiseUpdateCheckCompleted(false, null, $"Error checking for updates: {ex.Message}");
            return false;
        }
    }

    public void OpenDownloadPage()
    {
        var url = DownloadUrl ?? ReleasesPageUrl;
        Log.Information("Opening download page: {Url}", url);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open download page");
        }
    }

    private void RaiseUpdateCheckCompleted(bool updateAvailable, string? latestVersion, string? errorMessage)
    {
        UpdateCheckCompleted?.Invoke(this, new UpdateCheckEventArgs
        {
            UpdateAvailable = updateAvailable,
            LatestVersion = latestVersion,
            ErrorMessage = errorMessage
        });
    }

    /// <summary>
    /// Compares two semantic versions.
    /// Returns negative if v1 &lt; v2, zero if equal, positive if v1 &gt; v2.
    /// </summary>
    private static int CompareVersions(string v1, string v2)
    {
        // Handle pre-release versions (e.g., "0.1.0-beta")
        var v1Parts = v1.Split('-')[0].Split('.');
        var v2Parts = v2.Split('-')[0].Split('.');

        for (int i = 0; i < Math.Max(v1Parts.Length, v2Parts.Length); i++)
        {
            var part1 = i < v1Parts.Length && int.TryParse(v1Parts[i], out var p1) ? p1 : 0;
            var part2 = i < v2Parts.Length && int.TryParse(v2Parts[i], out var p2) ? p2 : 0;

            if (part1 != part2)
                return part1.CompareTo(part2);
        }

        // If base versions are equal, check for pre-release tags
        // A version without pre-release is considered newer than one with
        var v1HasPrerelease = v1.Contains('-');
        var v2HasPrerelease = v2.Contains('-');

        if (v1HasPrerelease && !v2HasPrerelease) return -1;
        if (!v1HasPrerelease && v2HasPrerelease) return 1;

        return 0;
    }

    // GitHub API response models
    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }
    }

    private class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
