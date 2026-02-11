using System.Diagnostics;
using System.IO;
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
            var assembly = Assembly.GetEntryAssembly();
            if (assembly != null)
            {
                var version = assembly.GetName().Version;
                if (version != null)
                {
                    return $"{version.Major}.{version.Minor}.{version.Build}";
                }

                var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (!string.IsNullOrEmpty(infoVersion))
                {
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

            var tagVersion = release.TagName?.TrimStart('v', 'V') ?? "0.0.0";
            LatestVersion = tagVersion;
            ReleaseNotes = release.Body;

            // Determine architecture suffix
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                _ => "x64"
            };

            // Find the matching Setup installer asset for this architecture
            DownloadUrl = release.Assets?
                .FirstOrDefault(a => a.Name?.Contains($"{arch}-Setup", StringComparison.OrdinalIgnoreCase) == true)?.BrowserDownloadUrl
                ?? release.Assets?
                    .FirstOrDefault(a => a.Name?.Contains("x64-Setup", StringComparison.OrdinalIgnoreCase) == true)?.BrowserDownloadUrl
                ?? release.HtmlUrl;

            IsUpdateAvailable = CompareVersions(CurrentVersion, LatestVersion) < 0;

            Log.Information("Update check complete. Current: {Current}, Latest: {Latest}, UpdateAvailable: {Available}, DownloadUrl: {Url}",
                CurrentVersion, LatestVersion, IsUpdateAvailable, DownloadUrl);

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

    public async Task<string?> DownloadInstallerAsync(IProgress<int>? progress = null)
    {
        if (string.IsNullOrEmpty(DownloadUrl))
        {
            Log.Warning("No download URL available");
            return null;
        }

        try
        {
            Log.Information("Downloading installer from: {Url}", DownloadUrl);

            // Use a longer timeout for downloads
            using var downloadClient = new HttpClient();
            downloadClient.DefaultRequestHeaders.Add("User-Agent", "Chronos-Updater");
            downloadClient.Timeout = TimeSpan.FromMinutes(10);

            using var response = await downloadClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var tempDir = Path.Combine(Path.GetTempPath(), "Chronos-Update");
            Directory.CreateDirectory(tempDir);

            // Extract filename from URL or use default
            var fileName = Path.GetFileName(new Uri(DownloadUrl).AbsolutePath);
            if (string.IsNullOrEmpty(fileName) || !fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                fileName = $"Chronos-{LatestVersion}-Setup.exe";

            var filePath = Path.Combine(tempDir, fileName);

            // Delete old file if it exists
            if (File.Exists(filePath))
                File.Delete(filePath);

            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

            var buffer = new byte[81920];
            long totalRead = 0;
            int bytesRead;
            int lastReportedPercent = -1;

            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (totalBytes > 0)
                {
                    var percent = (int)(totalRead * 100 / totalBytes);
                    if (percent != lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        progress?.Report(percent);
                    }
                }
            }

            Log.Information("Installer downloaded to: {Path} ({Bytes} bytes)", filePath, totalRead);
            return filePath;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download installer");
            return null;
        }
    }

    public void LaunchInstallerAndExit(string installerPath)
    {
        Log.Information("Launching installer: {Path}", installerPath);

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/CLOSEAPPLICATIONS",
                UseShellExecute = true
            });

            // Give the installer a moment to start, then exit
            Log.Information("Installer launched. Exiting application for update.");
            Log.CloseAndFlush();

            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch installer: {Path}", installerPath);
            throw;
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
        var v1Parts = v1.Split('-')[0].Split('.');
        var v2Parts = v2.Split('-')[0].Split('.');

        for (int i = 0; i < Math.Max(v1Parts.Length, v2Parts.Length); i++)
        {
            var part1 = i < v1Parts.Length && int.TryParse(v1Parts[i], out var p1) ? p1 : 0;
            var part2 = i < v2Parts.Length && int.TryParse(v2Parts[i], out var p2) ? p2 : 0;

            if (part1 != part2)
                return part1.CompareTo(part2);
        }

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
