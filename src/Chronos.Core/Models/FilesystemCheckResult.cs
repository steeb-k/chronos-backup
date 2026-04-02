namespace Chronos.Core.Models;

/// <summary>
/// Result of a post-restore filesystem consistency check on a VHDX image.
/// </summary>
public class FilesystemCheckResult
{
    /// <summary>
    /// True when no filesystem errors were detected (chkdsk exit code 0,
    /// or boot-sector signature present in fallback mode).
    /// </summary>
    public bool IsHealthy { get; init; }

    /// <summary>
    /// Human-readable summary of the check result.
    /// </summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>
    /// Exit code returned by chkdsk.exe. Null when chkdsk was unavailable
    /// and the fallback boot-sector check was used instead.
    /// </summary>
    public int? ChkdskExitCode { get; init; }

    /// <summary>
    /// True when the NTFS boot-sector fallback was used because chkdsk.exe
    /// was not available (e.g., WinPE minimal image).
    /// </summary>
    public bool UsedFallback { get; init; }
}
